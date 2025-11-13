sap.ui.define([
  "sap/ui/core/mvc/Controller",
  "sap/m/MessageToast",
  'sap/ui/model/json/JSONModel',
], function (Controller, MessageToast, JSONModel) {
  "use strict";
  const API = "/api";
  async function fetchJson(url, opts) {
    const res = await fetch(url, Object.assign({ headers: { 'Content-Type': 'application/json' } }, opts));
    if (!res.ok) throw new Error(await res.text());
    return res.status === 204 ? null : res.json();
  }
  return Controller.extend("cap.dotnet.ui.controller.Main", {
    onInit: function () {
      this._model = this.getOwnerComponent().getModel();
      if (this._model && this._model.setSizeLimit) {
        this._model.setSizeLimit(1000);
      }
      this.onRefresh();
    },
    onRefresh: async function () {
      try {
        const data = await fetchJson(API + "/products");
        const productsArray = Array.isArray(data) ? data : [];
        // Update the existing component model (created from manifest) instead of creating a new one
        this._model.setProperty('/products', productsArray);
        const productsFromModel = this._model.getProperty('/products');
        this._msg(`Loaded ${productsFromModel ? productsFromModel.length : 0} items`);
      } catch (e) {
        console.error("Main.controller onRefresh error", e);
        this._msg(e.message);
      }
    },
    onAdd: async function () {
      const name = this.byId("nameInput").getValue().trim();
      const price = parseFloat(this.byId("priceInput").getValue());
      if (!name || isNaN(price)) { this._msg("Enter name and price"); return; }
      try {
        await fetchJson(API + "/products", { method: "POST", body: JSON.stringify({ name, price }) });
        this.byId("nameInput").setValue("");
        this.byId("priceInput").setValue("");
        this.onRefresh();
        MessageToast.show("Created");
      } catch (e) {
        this._msg(e.message);
      }
    },
    onSave: async function (evt) {
      const ctx = evt.getSource().getBindingContext();
      const data = ctx.getObject();
      try {
        await fetchJson(API + "/products/" + data.id, { method: "PUT", body: JSON.stringify({ name: data.name, price: data.price }) });
        this.onRefresh();
        MessageToast.show("Saved");
      } catch (e) {
        this._msg(e.message);
      }
    },
    onDelete: async function (evt) {
      const ctx = evt.getSource().getBindingContext();
      const data = ctx.getObject();
      try {
        await fetchJson(API + "/products/" + data.id, { method: "DELETE" });
        this.onRefresh();
        MessageToast.show("Deleted");
      } catch (e) {
        this._msg(e.message);
      }
    },
    _msg: function (s) {
      const t = this.byId("msg");
      if (t) t.setText(s || "");
    }
  });
});
