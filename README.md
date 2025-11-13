SAP BTP Cloud Foundry sample: CAP-style (.NET SRV + HANA + APP)

Overview
- CAP-style layout: `app/` (SAP UI5 app built with UI5 Tooling), `db/` (CDS model), `srv/` (.NET 8 API), `approuter/` (routes + serves UI).
- CDS model (`db/schema.cds`) compiles to HANA HDI artifacts during MTA build.
- .NET 8 minimal API (`srv`) exposes Products CRUD over HANA using HDI binding.
- HTML5 App Repo: UI5 is built to a zip and deployed via a `com.sap.application.content` module to the HTML5 Apps Repository (host). A custom approuter serves via the HTML5 runtime and proxies `/api` to the .NET service.

Prerequisites
- SAP BTP Cloud Foundry org/space with SAP HANA Cloud or HANA service enabled.
- Cloud Foundry CLI (`cf`) and MultiApps plugin (`cf install-plugin -r CF-Community multiapps`).
- Node.js LTS and MBT (`npm i -g mbt`) for building the MTA and CDS.
- .NET 8 SDK for optional local runs of `srv`.

Local build (optional)
1) Build CDS and UI5 locally (optional)
- CDS: `npm install && npm run build`
- UI5: `cd app && npm install && npm run build:cf`

2) Restore and run API (set HANA env)
   - Set `HANA_HOST`, `HANA_PORT`, `HANA_USER`, `HANA_PASSWORD`, `HANA_SCHEMA`.
   - `cd srv && dotnet restore && dotnet run`
   - API: http://localhost:5182 (actual port on run) → `/api/products`
   - Swagger proxied in CF through approuter at `/swagger`.

Deploy to Cloud Foundry via MTA
1) Login to CF and target your space
   - `cf login` (or `cf api`, `cf auth`, `cf target` as needed)

2) Build the MTAR
   - From repo root: `mbt build -p=cf -t mta_archives`

3) Deploy
- `cf deploy mta_archives/DotNetBTP_1.0.0.mtar`

4) Access the app
   - `cf apps` to find the `approuter` route. Open `https://<approuter-route>`.
- UI5 app is deployed to the HTML5 Apps Repository and served by the approuter via the HTML5 Runtime; API is reachable under `/api/*`.

Notes
- CAP-style folders: `app/`, `db/` (CDS), `srv/` (.NET), `approuter/`.
- UI is SAP UI5 built with UI5 Tooling (OpenUI5 CDN for runtime). Switch to SAPUI5 framework in `ui5.yaml` if you use the SAP registry.
- The `srv` module binds to the HDI container and reads HANA credentials from `VCAP_SERVICES`.
- Connection uses TLS by default. If you get certificate validation errors, set `HANA_SSL_VALIDATE=false` as an app env (not recommended for production).
- DB model in `db/schema.cds` compiles to HANA tables (e.g., `Products` with fields `ID`, `name`, `price`, `createdAt`, ... from `managed`).

Common CF commands
- Re-deploy: `cf deploy mta_archives/DotNetBTP_1.0.0.mtar -f`
- Undeploy: `cf undeploy DotNetBTP --delete-services -f`
- Logs: `cf logs approuter --recent` / `cf logs dotnet-srv --recent`
