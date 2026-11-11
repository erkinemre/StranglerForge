# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
ARG PROJECT
RUN test -n "$PROJECT" && dotnet restore "$PROJECT"
RUN dotnet publish "$PROJECT" -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build --chown=app:app /app/publish ./
USER app
EXPOSE 8080
ARG ASSEMBLY
ENV APP_ASSEMBLY=$ASSEMBLY
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_ASSEMBLY\""]