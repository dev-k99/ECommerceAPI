# -------- BUILD STAGE --------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copy csproj and restore
COPY ECommerceAPI/ECommerceAPI.csproj ECommerceAPI/
RUN dotnet restore ECommerceAPI/ECommerceAPI.csproj

# Copy everything else
COPY . .
WORKDIR /app/ECommerceAPI

# Publish
RUN dotnet publish -c Release -o /app/out

# -------- RUNTIME STAGE --------
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Copy published output
COPY --from=build /app/out .

# Render listens on port 10000
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

ENTRYPOINT ["dotnet", "ECommerceAPI.dll"]
