# Build context: repository root.
#   podman build -f src/SkailPurchaseApproval.Web/Dockerfile -t purchase-approval-web .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/SkailPurchaseApproval.Contracts/SkailPurchaseApproval.Contracts.csproj src/SkailPurchaseApproval.Contracts/
COPY src/SkailPurchaseApproval.Web/SkailPurchaseApproval.Web.csproj src/SkailPurchaseApproval.Web/
RUN dotnet restore src/SkailPurchaseApproval.Web/SkailPurchaseApproval.Web.csproj
COPY src/SkailPurchaseApproval.Contracts src/SkailPurchaseApproval.Contracts
COPY src/SkailPurchaseApproval.Web src/SkailPurchaseApproval.Web
RUN dotnet publish src/SkailPurchaseApproval.Web/SkailPurchaseApproval.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SkailPurchaseApproval.Web.dll"]
