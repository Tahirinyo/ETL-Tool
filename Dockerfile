FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["EtlTool.Domain/EtlTool.Domain.csproj", "EtlTool.Domain/"]
COPY ["EtlTool.Application/EtlTool.Application.csproj", "EtlTool.Application/"]
COPY ["EtlTool.Infrastructure/EtlTool.Infrastructure.csproj", "EtlTool.Infrastructure/"]
COPY ["EtlTool.Web/EtlTool.Web.csproj", "EtlTool.Web/"]
RUN dotnet restore "EtlTool.Web/EtlTool.Web.csproj"

COPY . .
RUN dotnet publish "EtlTool.Web/EtlTool.Web.csproj" --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .
RUN mkdir -p /app/App_Data && chown -R "$APP_UID":"$APP_UID" /app
USER $APP_UID

ENTRYPOINT ["dotnet", "EtlTool.Web.dll"]
