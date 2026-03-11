FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["nuget.config", "."]
COPY ["ProDataStack.CDP.Segmentation.Api/ProDataStack.CDP.Segmentation.Api.csproj", "ProDataStack.CDP.Segmentation.Api/"]
ARG GITHUB_TOKEN
RUN dotnet nuget remove source "ProDataStack" || true
RUN dotnet nuget add source "https://nuget.pkg.github.com/ProDataStack/index.json" \
    --name "ProDataStack" \
    --username "ProDataStack" \
    --password "${GITHUB_TOKEN}" \
    --store-password-in-clear-text
RUN dotnet restore "ProDataStack.CDP.Segmentation.Api/ProDataStack.CDP.Segmentation.Api.csproj"
COPY . .
WORKDIR "/src/ProDataStack.CDP.Segmentation.Api"
RUN dotnet build "ProDataStack.CDP.Segmentation.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ProDataStack.CDP.Segmentation.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ProDataStack.CDP.Segmentation.Api.dll"]
