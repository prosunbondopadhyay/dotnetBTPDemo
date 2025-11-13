# Deployment Guide: Using BTP Destination Service for SAP NuGet Feed

## Summary of Changes

The following updates have been made to use **SAP BTP's Destination service** for secure access to the SAP NuGet feed:

1. **mta.yaml**:
   - Updated `dotnet-destination` resource to include a `sap-nuget-feed` destination
   - This destination is automatically created when the MTA is deployed

2. **srv/NuGet.config**:
   - Updated with documentation on how the CF .NET Connector routes requests through the Destination service
   - Still references `https://nuget.sap.com/` directly (routing is handled transparently)

3. **docs/SAP_NUGET_DESTINATION.md**:
   - Comprehensive guide on the Destination service integration
   - Troubleshooting steps for common issues

4. **srv/setup-nuget-destination.ps1**:
   - Reference script explaining the Destination integration

## Next Steps: Deploy to Cloud Foundry

### Step 1: Login to Cloud Foundry

```bash
cf login -a https://api.cf.<region>.hana.ondemand.com
# Enter your SAP BTP credentials when prompted
```

### Step 2: Target Your Space

```bash
cf target -o <org-name> -s <space-name>
```

### Step 3: Deploy the MTA

```bash
cf deploy mta_archive\DotNetBTP_1.0.0.mtar
```

Monitor the deployment:

```bash
# In another terminal, watch the logs
cf logs --recent
```

### Step 4: Verify the Destination Service Instance

After deployment completes, verify the Destination service was created:

```bash
cf services
```

You should see:
- `dotnet-destination` (instance of `destination` service)

### Step 5: Configure the SAP NuGet Feed Destination (if credentials required)

1. Open **BTP Console** → your subaccount
2. Navigate to **Destinations**
3. Find **sap-nuget-feed** (auto-created by MTA deployment)
4. Click **Edit**

If the SAP NuGet feed requires authentication:
- Change **Authentication** from "NoAuthentication" to:
  - **BasicAuthentication** (if username/password provided by SAP)
  - **OAuth2ClientCredentials** (if OAuth token required)
  - **mTLS** (if certificate-based)
- Fill in the credentials/certificates
- Click **Save**

If the feed is publicly accessible, leave as **NoAuthentication**.

### Step 6: Verify Package Restore Works

Check the `dotnet-srv` app logs to confirm successful package restore:

```bash
cf logs dotnet-srv --recent | grep -i "nuget\|restore\|publish"
```

You should see:
- ✅ Successful restore of `Sap.Data.Hana.Core`
- ✅ Successful `dotnet publish` completion
- ❌ No `NU1101` or `NU1301` errors

### Step 7: Verify App Deployment

Once all modules are staged and running:

```bash
cf apps
```

Expected output:
```
name                  requested state   instances   memory   disk   urls
dotnet-srv            started           1/1         512M     1G     dotnetbtp-srv-<random>.cfapps.<region>.hana.ondemand.com
approuter             started           1/1         256M     512M   dotnetbtp-<random>.cfapps.<region>.hana.ondemand.com
db-deployer           stopped           0/1         256M     256M
ui-deployer           stopped           0/1         256M     256M
```

---

## Troubleshooting

### Issue: "Name or service not known (nuget.sap.com:443)" during staging

**Root Cause**: The Destination service is not properly configured or the buildpack cannot access it.

**Solution**:
1. Verify Destination service instance exists:
   ```bash
   cf service dotnet-destination
   ```
2. Check the `sap-nuget-feed` destination is created:
   - Open BTP Console > Destinations
   - Verify `sap-nuget-feed` exists and **Status** is **Success**
3. Check the buildpack logs:
   ```bash
   cf logs dotnet-srv --recent | tail -100
   ```
   - Look for connection attempts to the Destination service

### Issue: "error NU1301: Failed to retrieve information about 'Sap.Data.Hana.Core'"

**Root Cause**: Either the destination URL is incorrect, or authentication failed.

**Solution**:
1. Click the `sap-nuget-feed` destination in BTP Console
2. Click **Check Connection** (button at the bottom)
3. If it fails, verify:
   - URL is correct: `https://nuget.sap.com`
   - Authentication credentials (if applicable) are correct
4. Edit and re-save the destination
5. Redeploy the app (no need to rebuild):
   ```bash
   cf re-push dotnet-srv
   # Or full redeploy for all modules:
   cf deploy mta_archive\DotNetBTP_1.0.0.mtar
   ```

### Issue: CF deployment times out or fails

**Solution**:
1. Check available quota:
   ```bash
   cf org <org-name>
   ```
2. Check service instance creation status:
   ```bash
   cf service-instance dotnet-destination
   ```
3. If services are slow to provision, wait a moment and retry:
   ```bash
   cf deploy mta_archive\DotNetBTP_1.0.0.mtar --retries 5
   ```

---

## Monitoring & Debugging

### View All Service Bindings

```bash
cf apps
cf env dotnet-srv
# Look for VCAP_SERVICES > destination
```

### View Destination Service Logs (if available)

Some SAP platforms provide audit logs for Destination service access:
- BTP Console > Subaccount > Monitoring > Application Logs
- Check for `destination-service` entries

### Test Package Restore Locally (optional)

If you have SAP NuGet feed credentials and want to test locally:

```bash
# Add credentials to local NuGet.config (NOT the repo version!)
# Then restore:
dotnet restore srv/DotNetBTP.Srv.csproj -v diag
```

---

## Summary

Your .NET application now uses **SAP BTP's Destination service** for secure, credential-managed access to the SAP NuGet feed. The buildpack automatically handles routing through the Destination service, so no additional code changes are needed.

For questions or issues, refer to:
- [docs/SAP_NUGET_DESTINATION.md](./SAP_NUGET_DESTINATION.md)
- [SAP BTP Destination Service Documentation](https://help.sap.com/viewer/cca5c5bd52d04552b0e5dcbc74bba7b3/Cloud/en-US)
