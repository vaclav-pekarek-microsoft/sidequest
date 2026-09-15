SET NOCOUNT ON;

DECLARE @databaseId int = DB_ID(N'SidequestBrowserCi');
IF @databaseId IS NULL
    THROW 50000, 'The isolated browser diagnostics database does not exist.', 1;

SELECT report.Event.query('(data/value/deadlock)[1]') AS [*]
FROM sys.dm_xe_session_targets AS target
INNER JOIN sys.dm_xe_sessions AS session ON target.event_session_address = session.address
CROSS APPLY (SELECT CAST(target.target_data AS xml)) AS buffer(Data)
CROSS APPLY buffer.Data.nodes('/RingBufferTarget/event[@name="xml_deadlock_report"]') AS report(Event)
WHERE session.name = N'system_health'
    AND target.target_name = N'ring_buffer'
    AND report.Event.exist('data/value/deadlock/resource-list/*[@dbid=sql:variable("@databaseId")]') = 1
FOR XML PATH(''), ROOT('deadlocks'), TYPE;
