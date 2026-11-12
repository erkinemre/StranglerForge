using Npgsql;
using RabbitMQ.Client;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Xunit;

namespace Deployment.LiveTests;

public sealed class ContainerConnectivityTests
{
    [Fact(Timeout = 120_000)]
    public async Task IsolatedPostgresAndRabbitMqAreReady()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:18").Build();
        await using var rabbit = new RabbitMqBuilder("rabbitmq:4-management").Build();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await Task.WhenAll(postgres.StartAsync(timeout.Token), rabbit.StartAsync(timeout.Token));

        await using var database = new NpgsqlConnection(postgres.GetConnectionString());
        await database.OpenAsync(timeout.Token);
        await using var command = new NpgsqlCommand("SELECT 1", database);
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync(timeout.Token)));

        var connectionFactory = new ConnectionFactory { Uri = new Uri(rabbit.GetConnectionString()) };
        await using var broker = await connectionFactory.CreateConnectionAsync(timeout.Token);
        Assert.True(broker.IsOpen);
        await using var channel = await broker.CreateChannelAsync(cancellationToken: timeout.Token);
        var queue = "phase09-" + Guid.NewGuid().ToString("N");
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: true, autoDelete: true, cancellationToken: timeout.Token);
        var payload = Guid.NewGuid().ToString("N");
        await channel.BasicPublishAsync(exchange: string.Empty, routingKey: queue, body: System.Text.Encoding.UTF8.GetBytes(payload), cancellationToken: timeout.Token);
        var received = await channel.BasicGetAsync(queue, autoAck: true, cancellationToken: timeout.Token);
        Assert.NotNull(received);
        Assert.Equal(payload, System.Text.Encoding.UTF8.GetString(received.Body.ToArray()));
    }
}