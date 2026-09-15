SET NOCOUNT ON;

IF DB_ID(N'SidequestBrowserCi') IS NULL
    THROW 50000, 'The isolated browser diagnostics database does not exist.', 1;

CREATE EVENT SESSION SidequestBrowserDiagnostics ON SERVER
    ADD EVENT sqlserver.xml_deadlock_report
    ADD TARGET package0.ring_buffer(SET max_memory = 4096)
    WITH (MAX_MEMORY = 4096 KB, MAX_DISPATCH_LATENCY = 1 SECONDS, STARTUP_STATE = OFF);

ALTER EVENT SESSION SidequestBrowserDiagnostics ON SERVER STATE = START;
