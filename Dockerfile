FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ChatApp.Web/ChatApp.Web.csproj src/ChatApp.Web/
RUN dotnet restore src/ChatApp.Web/ChatApp.Web.csproj
COPY . .
RUN dotnet publish src/ChatApp.Web/ChatApp.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    CHATAPP_DATA_DIR=/data
COPY --from=build /app/publish .
# Create /data owned by the container's non-root user BEFORE starting.
# A fresh volume copies this folder's ownership, so the app can write its
# database files into it on first boot.
RUN mkdir -p /data && chown app:app /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ChatApp.Web.dll"]
