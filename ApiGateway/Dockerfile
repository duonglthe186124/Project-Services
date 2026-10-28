# Giai đoạn 1: Build mã nguồn bằng .NET 8 SDK
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["ApiGateway.csproj", "./"]
RUN dotnet restore "ApiGateway.csproj"

COPY . .
RUN dotnet publish "ApiGateway.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Giai đoạn 2: Tạo image chạy thực tế bằng .NET 8 ASP.NET Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ApiGateway.dll"]
