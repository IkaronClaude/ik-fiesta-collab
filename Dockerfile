# ik-fiesta-collab — the `fiesta` content toolkit as an image.
# Converts a JSON project to/from SHN/txt (and SQL query/edit/validate). Used by
# the ik-fiesta-collab-pipelines build step to turn JSON content into SHN/txt:
#   docker run --rm -v "$PWD/content:/project" ghcr.io/<owner>/ik-fiesta-collab \
#       build --env server
# (mount your fiesta project at /project — where mimir.json lives — build output
#  lands in the project's build/ dir).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Fiesta.Collab.Cli/Fiesta.Collab.Cli.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
COPY --from=build /app /opt/fiesta
WORKDIR /project
ENTRYPOINT ["dotnet", "/opt/fiesta/fiesta.dll"]
