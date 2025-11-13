using { managed } from '@sap/cds/common';

entity Products : managed {
  key ID    : Integer;
  name      : String(255);
  price     : Decimal(13,2);
}

