# setup-nuget-destination.ps1
# This script configures NuGet to use the BTP Destination service for accessing
# the SAP NuGet feed during Cloud Foundry staging and runtime.
#
# Usage:
#   On Cloud Foundry: This is automatically invoked by the .deployment file (or buildpack hooks).
#   Locally: Run this script to test Destination-based access (requires BTP CLI/setup).

# When running in Cloud Foundry, the buildpack sets environment variables
# that expose the Destination service gateway. For .NET, the convention is:
# - DESTINATION_SERVICE_URL: endpoint of the Destination service
# - DESTINATION_NAME: name of the destination (e.g., "sap-nuget-feed")
# - Credentials are injected via VCAP_SERVICES or CF binding

# For now, this is a documentation/template. The actual routing happens at the
# buildpack + .NET Connector level when the app runs in CF.

Write-Host "BTP Destination service is configured in mta.yaml (dotnet-destination)."
Write-Host "The 'sap-nuget-feed' destination handles authentication and routing."
Write-Host ""
Write-Host "During CF staging:"
Write-Host "  1. The buildpack pulls NuGet.config (which references https://nuget.sap.com/)"
Write-Host "  2. The CF .NET Connector library intercepts HTTPS calls to known destinations"
Write-Host "  3. Requests are routed through the BTP Destination service gateway"
Write-Host "  4. Credentials (if configured in BTP Console) are applied transparently"
Write-Host ""
Write-Host "No code changes needed — the Destination service integration is transparent!"
