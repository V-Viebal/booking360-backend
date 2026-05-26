FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8101

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["nuget.config", "./"]
COPY ["Booking360.Api/Booking360.Api.csproj", "Booking360.Api/"]

RUN dotnet restore "./Booking360.Api/Booking360.Api.csproj"

COPY ["Booking360.Api/", "Booking360.Api/"]

WORKDIR "/src/Booking360.Api"
RUN dotnet build "./Booking360.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build /p:OpenApiGenerateDocuments=false

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Booking360.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:OpenApiGenerateDocuments=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Booking360.Api.dll"]