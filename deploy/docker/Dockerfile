FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
ARG PROJECT
RUN dotnet restore "$PROJECT"
RUN dotnet publish "$PROJECT" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
ARG ASSEMBLY
ENV APP_ASSEMBLY=$ASSEMBLY
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_ASSEMBLY\""]
