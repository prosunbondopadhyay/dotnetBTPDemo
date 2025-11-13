sap.ui.define([
  "sap/ui/core/UIComponent"
], function (UIComponent) {
  "use strict";
  return UIComponent.extend("cap.dotnet.ui.Component", {
    metadata: { manifest: "json" },
    init: function () {
      UIComponent.prototype.init.apply(this, arguments);
      // Default model is created from manifest (sap.ui5.models)
    }
  });
});
