-- Adds a "CAN DBC Feeder" ECU to DigitalDyno so the feeder's channel values are labelled/scaled.
-- Apply to the ECU database:  C:\Users\<user>\Documents\<ver>\databases\dyno-e.db
--   1. BACK UP dyno-e.db first.
--   2. Pick a ProtocolID not already in sys_ecu (existing: 1,2,3,5,6,7,11,12,13,14). Using 20.
--   3. UnitID values must reference real sys_unit rows -- confirm ids with:
--        SELECT ID, Name FROM sys_unit WHERE Name LIKE '%rpm%' OR Name LIKE '%lambda%';
--      then substitute below (0 is a placeholder).
-- Run:  sqlite3 dyno-e.db < feeder-ecu.sql

BEGIN;

INSERT INTO sys_ecu (Name, Description, TachoChannel, ProtocolID, ActiveChans)
VALUES ('CAN DBC Feeder (Haltech WB2)', 'Live CAN via DynoCanFeeder', 1, 20, '');

-- ChannelNo must match the feeder.config.json Routes. Scale/Offset must match too:
-- the feeder sends raw = round((physical - Offset)/Scale); the dyno shows raw*Scale + Offset.
INSERT INTO sys_ecuchanneldef
  (ProtocolID, ChannelNo, ChannelName, ShortName, Description, UnitID, UnitName, Scale, Offset, DataType)
VALUES
  (20, 1,  'Engine RPM',        'RPM',    'From DBC Engine_RPM',       0, 'rpm',    1.0,   0.0, 2),
  (20, 10, 'Wideband Lambda',   'Lambda', 'From DBC Wideband_Overall', 0, 'lambda', 0.001, 0.0, 2);

COMMIT;
