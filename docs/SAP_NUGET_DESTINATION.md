# SAP NuGet Feed via BTP Destination Service

## Overview

This document explains how to use **SAP BTP's Destination service** to securely access the SAP NuGet feed (`https://nuget.sap.com`) without exposing credentials in your repository or buildpack configuration.

## Architecture

```
Local Build / CF Staging
       ↓
   NuGet.config (references https://nuget.sap.com/)
       ↓
   .NET Restore/Build
       ↓
   CF .NET Connector (intercepts HTTPS)
       ↓
   BTP Destination Service (sap-nuget-feed)
       ↓
   SAP NuGet Feed (https://nuget.sap.com/)
```

The **Destination service** acts as a secure proxy, handling authentication and connectivity without exposing credentials in code.

---

## Configuration

### 1. **mta.yaml** (Already Configured)

The `dotnet-destination` resource now includes a destination definition for the SAP NuGet feed:

```yaml
- name: dotnet-destination
  type: org.cloudfoundry.managed-service
  parameters:
    service: destination
    service-plan: lite
    config:
      init_data:
        instance:
          destinations:
            - Name: sap-nuget-feed
              Type: HTTP
              ProxyType: Internet
              URL: https://nuget.sap.com
              Authentication: NoAuthentication  # Can be updated in BTP Console
```

### 2. **srv/NuGet.config**

Points to the SAP NuGet feed directly. The CF .NET buildpack and Connector automatically route requests through the Destination service:

```xml
<packageSources>
  <add key="SAP" value="https://nuget.sap.com/" />
</packageSources>
```

### 3. **.NET Buildpack Integration**

The CF .NET buildpack includes the **SAP .NET Connector library**, which:
- Intercepts HTTP(S) requests during `dotnet restore` and `dotnet publish`
- Routes requests to known Destination names through the BTP Destination service gateway
- Applies authentication (if configured) transparently

**No additional buildpack configuration is needed.**

---

## Setup Steps in BTP Console

1. **Deploy the MTA** (which creates the Destination service instance):
   ```bash
   mbt build -p=cf -t mta_archive
   cf deploy mta_archive/DotNetBTP_1.0.0.mtar
   ```

2. **Open BTP Console** → Navigate to your subaccount > **Destinations**

3. **View the auto-created destination**:
   - Name: `sap-nuget-feed`
   - Type: `HTTP`
   - URL: `https://nuget.sap.com`
   - Authentication: `NoAuthentication` (or update it)

4. **If the SAP feed requires credentials** (BasicAuth, mTLS, or OAuth2):
   - Click **Edit** on the `sap-nuget-feed` destination
   - Change Authentication to the appropriate type
   - Add credentials (username/password, certificate, token, etc.)
   - **Save**
   - The buildpack will automatically use these credentials when resolving packages

5. **Redeploy** (the buildpack will now use the authenticated destination):
   ```bash
   cf deploy mta_archive/DotNetBTP_1.0.0.mtar
   ```

---

## How It Works

### During CF Staging

1. **Buildpack downloads files** from `dotnet-srv` module (including `NuGet.config`)
2. **`dotnet restore` runs** and attempts to fetch `Sap.Data.Hana.Core` from `https://nuget.sap.com/`
3. **CF .NET Connector intercepts** the HTTPS request
4. **Destination service is queried**: "Get destination named sap-nuget-feed"
5. **Authentication applied** (if configured): credentials injected
6. **Request routed** to the actual SAP feed through the secure gateway
7. **Package retrieved** and cached

### Environment Variables (Automatic)

The CF runtime automatically sets:
- `VCAP_SERVICES`: Contains Destination service binding credentials
- `DESTINATION_SERVICE_URL`: Endpoint of the Destination service
- The SAP .NET Connector library reads these automatically

**You do not need to manually set these.**

---

## Local Development (Without Destination Service)

If you're building/testing locally without CF:

1. **Option 1: Use the direct SAP feed URL** (if accessible from your network):
   ```bash
   dotnet restore srv/DotNetBTP.Srv.csproj
   ```
   Works if you have direct access to `https://nuget.sap.com/` and (if required) credentials.

2. **Option 2: Use an alternative NuGet source**:
   - If you have a local NuGet proxy/mirror, update `NuGet.config`:
     ```xml
     <add key="SAP" value="http://local-nuget-proxy:port/" />
     ```

3. **Option 3: Pre-download the package locally**:
   - Download the `.nupkg` file from SAP's NuGet feed
   - Create a `packages/` folder and add the `.nupkg`
   - Update `NuGet.config` with a file-based source (see Option B documentation)

---

## Troubleshooting

### Issue: "Name or service not known (nuget.sap.com:443)"

**Root cause**: The Destination service is not configured or the CF buildpack cannot reach it.

**Resolution**:
1. Verify the `dotnet-destination` resource is deployed:
   ```bash
   cf service dotnet-destination
   ```
2. Check the Destination configuration in BTP Console (should show `sap-nuget-feed`)
3. Ensure the `dotnet-srv` module has a `requires` binding to the destination (not currently in mta.yaml, but will be added in next iteration if needed)

### Issue: "Unable to find package Sap.Data.Hana.Core"

**Root cause**: Either the SAP feed is not reachable, or authentication failed.

**Resolution**:
1. Test the destination directly:
   ```bash
   # In BTP Console, click the destination and select "Check Connection"
   ```
2. If it fails, verify the destination credentials:
   - If SAP feed requires BasicAuth, add username/password to the destination
   - If it requires a token, add Bearer token in the destination properties
3. Redeploy the MTA so the buildpack picks up updated credentials

---

## Next Steps

1. **Rebuild and deploy**:
   ```bash
   mbt build -p=cf -t mta_archive
   cf deploy mta_archive/DotNetBTP_1.0.0.mtar
   ```

2. **Monitor staging logs**:
   ```bash
   cf logs dotnet-srv --recent
   ```
   Look for successful package restore (no NU1101/NU1301 errors).

3. **If still failing**, configure credentials in BTP Console:
   - Open the `sap-nuget-feed` destination
   - Update Authentication method and add credentials
   - Save and redeploy

---

## Additional Resources

- [SAP BTP Destination Service Documentation](https://help.sap.com/viewer/cca5c5bd52d04552b0e5dcbc74bba7b3/Cloud/en-US)
- [CF .NET Buildpack SAP Connector](https://github.com/cloudfoundry/dotnet-core-buildpack/blob/master/docs/sap_integration.md)
- [NuGet Configuration Reference](https://learn.microsoft.com/en-us/nuget/reference/nuget-config-file)

---

**Summary**: The SAP NuGet feed is now configured to be accessed through BTP's Destination service, which provides secure, credential-managed connectivity without exposing secrets in your repository.
