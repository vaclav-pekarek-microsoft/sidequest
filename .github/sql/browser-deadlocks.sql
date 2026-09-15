SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @databaseId int = DB_ID(N'SidequestBrowserCi');
IF @databaseId IS NULL
    THROW 50000, 'The isolated browser diagnostics database does not exist.', 1;

DECLARE @buffer xml = (
    SELECT CAST(target.target_data AS xml)
    FROM sys.dm_xe_session_targets AS target
    INNER JOIN sys.dm_xe_sessions AS session ON target.event_session_address = session.address
    WHERE session.name = N'system_health' AND target.target_name = N'ring_buffer'
);
IF @buffer IS NULL
    THROW 50000, 'The system_health ring buffer is unavailable for diagnostics.', 1;

SELECT @buffer.query('<deadlocks reports="{count(/RingBufferTarget/event[@name="xml_deadlock_report"])}" truncated="{/RingBufferTarget/@truncated}">{
    /RingBufferTarget/event[@name="xml_deadlock_report"]/data/value/deadlock[
        resource-list/*/@dbid = sql:variable("@databaseId")
        or process-list/process/@currentdb = sql:variable("@databaseId")
    ]
}</deadlocks>');
