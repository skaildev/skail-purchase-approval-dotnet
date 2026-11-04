# Build context: repository root.
#   podman build -f src/SkailPurchaseApproval.Worker/Dockerfile -t purchase-approval-worker .
# dotnet publish runs in the SDK image; the final image only needs the .NET runtime.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/SkailPurchaseApproval.Contracts/SkailPurchaseApproval.Contracts.csproj src/SkailPurchaseApproval.Contracts/
COPY src/SkailPurchaseApproval.Worker/SkailPurchaseApproval.Worker.csproj src/SkailPurchaseApproval.Worker/
RUN dotnet restore src/SkailPurchaseApproval.Worker/SkailPurchaseApproval.Worker.csproj
COPY src/SkailPurchaseApproval.Contracts src/SkailPurchaseApproval.Contracts
COPY src/SkailPurchaseApproval.Worker src/SkailPurchaseApproval.Worker
RUN dotnet publish src/SkailPurchaseApproval.Worker/SkailPurchaseApproval.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "SkailPurchaseApproval.Worker.dll"]
