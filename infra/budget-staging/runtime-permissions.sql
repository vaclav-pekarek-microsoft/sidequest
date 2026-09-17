-- Input: @RuntimeObjectId uniqueidentifier, obtained from the ARM-verified
-- sidequest-app user-assigned identity by bootstrap.ps1. Execute as Entra admin.
-- Directory resolution is intentionally delegated to FROM EXTERNAL PROVIDER;
-- failure is a hard stop, never a reason to grant Graph directory permissions.
-- https://learn.microsoft.com/sql/t-sql/statements/create-user-transact-sql
-- Do not substitute hand-encoded SID/TYPE: Fabric/service-principal login SID
-- documentation differs from Azure SQL contained-user object-ID mapping.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() COLLATE Latin1_General_100_BIN2 <> N'sidequest' OR USER_NAME() <> N'dbo'
    THROW 51010, 'Unexpected runtime bootstrap context.', 1;
IF @RuntimeObjectId IS NULL OR @RuntimeObjectId = '00000000-0000-0000-0000-000000000000'
    THROW 51011, 'Missing runtime identity.', 1;

-- The current EF snapshot contains 26 application tables, plus its history table.
-- A model change must update this reviewed allowlist, not broaden schema access.
DECLARE @Tables TABLE (Name sysname PRIMARY KEY);
INSERT @Tables (Name) VALUES
    (N'Administrators'), (N'ApplicationSettings'), (N'AuditEntries'),
    (N'BulkOperations'), (N'BulkRecipients'), (N'CalendarDeliveryStates'),
    (N'Events'), (N'EventInvitations'), (N'EventMemberships'),
    (N'MembershipRequests'), (N'EventNotificationPreferences'), (N'EventOwners'),
    (N'EventStatusHistory'), (N'MediaAssets'), (N'Notifications'),
    (N'NotificationDeliveries'), (N'NotificationPreferences'), (N'NotificationTemplates'),
    (N'OutboxMessages'), (N'Quests'), (N'QuestInvitations'), (N'QuestOwners'),
    (N'Participations'), (N'QuestStatusHistory'), (N'ScheduledWork'), (N'Users');
IF EXISTS (SELECT 1 FROM @Tables WHERE OBJECT_ID(N'dbo.' + QUOTENAME(Name), N'U') IS NULL)
    OR OBJECT_ID(N'dbo.__EFMigrationsHistory', N'U') IS NULL
    THROW 51012, 'Reviewed application schema is incomplete.', 1;
IF EXISTS (
    SELECT 1 FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE t.is_ms_shipped = 0
      AND (s.name <> N'dbo' OR (t.name <> N'__EFMigrationsHistory'
          AND NOT EXISTS (SELECT 1 FROM @Tables a WHERE a.Name = t.name))))
    THROW 51013, 'Unreviewed application table exists.', 1;

IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'sidequest-app'
    AND (type <> 'E' OR authentication_type <> 4
        OR sid <> CONVERT(binary(16), @RuntimeObjectId)))
    THROW 51014, 'Existing runtime user identity mismatch.', 1;
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name <> N'sidequest-app'
    AND sid = CONVERT(binary(16), @RuntimeObjectId))
    THROW 51015, 'Runtime identity already has another database alias.', 1;
IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'sidequest_runtime'
    AND (type <> 'R' OR owning_principal_id <> DATABASE_PRINCIPAL_ID(N'dbo')))
    THROW 51016, 'Existing runtime role mismatch.', 1;

DECLARE @UserId int = DATABASE_PRINCIPAL_ID(N'sidequest-app');
DECLARE @RoleId int = DATABASE_PRINCIPAL_ID(N'sidequest_runtime');
IF EXISTS (SELECT 1 FROM sys.database_role_members
    WHERE (member_principal_id = @UserId AND (@RoleId IS NULL OR role_principal_id <> @RoleId))
       OR member_principal_id = @RoleId
       OR (role_principal_id = @RoleId AND (@UserId IS NULL OR member_principal_id <> @UserId)))
    THROW 51017, 'Unexpected runtime role membership.', 1;
IF EXISTS (SELECT 1 FROM sys.schemas WHERE principal_id IN (@UserId, @RoleId))
    OR EXISTS (SELECT 1 FROM sys.objects WHERE principal_id IN (@UserId, @RoleId))
    OR EXISTS (SELECT 1 FROM sys.database_principals WHERE owning_principal_id IN (@UserId, @RoleId))
    THROW 51018, 'Runtime principal unexpectedly owns a securable.', 1;

-- Reject drift instead of silently revoking, overwriting, or preserving privilege.
IF EXISTS (
    SELECT 1 FROM sys.database_permissions p
    WHERE p.grantee_principal_id IN (@UserId, @RoleId)
      AND NOT (
          (@UserId IS NOT NULL AND p.grantee_principal_id = @UserId AND p.class = 0 AND p.permission_name = N'CONNECT' AND p.state = 'G')
          OR (@RoleId IS NOT NULL AND p.grantee_principal_id = @RoleId AND p.class = 1 AND p.minor_id = 0 AND p.state = 'G'
              AND ((p.major_id = OBJECT_ID(N'dbo.__EFMigrationsHistory') AND p.permission_name = N'SELECT')
                  OR (p.permission_name IN (N'SELECT', N'INSERT', N'UPDATE', N'DELETE')
                      AND EXISTS (SELECT 1 FROM @Tables t WHERE p.major_id = OBJECT_ID(N'dbo.' + QUOTENAME(t.Name))))))))
    THROW 51019, 'Unexpected existing runtime permission.', 1;

IF @UserId IS NULL
BEGIN
    DECLARE @CreateUser nvarchar(max) = N'CREATE USER [sidequest-app] FROM EXTERNAL PROVIDER WITH OBJECT_ID = '''
        + CONVERT(nvarchar(36), @RuntimeObjectId) + N''';';
    EXEC sys.sp_executesql @CreateUser;
END;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'sidequest-app'
    AND type = 'E' AND authentication_type = 4 AND sid = CONVERT(binary(16), @RuntimeObjectId))
    THROW 51020, 'Resolved runtime user identity mismatch.', 1;
IF @RoleId IS NULL
    CREATE ROLE [sidequest_runtime] AUTHORIZATION [dbo];
IF NOT EXISTS (SELECT 1 FROM sys.database_role_members
    WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'sidequest_runtime')
      AND member_principal_id = DATABASE_PRINCIPAL_ID(N'sidequest-app'))
    ALTER ROLE [sidequest_runtime] ADD MEMBER [sidequest-app];

GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.Administrators TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.ApplicationSettings TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.AuditEntries TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.BulkOperations TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.BulkRecipients TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.CalendarDeliveryStates TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.Events TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.EventInvitations TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.EventMemberships TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.MembershipRequests TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.EventNotificationPreferences TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.EventOwners TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.EventStatusHistory TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.MediaAssets TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.Notifications TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.NotificationDeliveries TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.NotificationPreferences TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.NotificationTemplates TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.OutboxMessages TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.Quests TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.QuestInvitations TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.QuestOwners TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.Participations TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.QuestStatusHistory TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.ScheduledWork TO [sidequest_runtime];
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.Users TO [sidequest_runtime];
GRANT SELECT ON OBJECT::dbo.__EFMigrationsHistory TO [sidequest_runtime];

-- Non-mutating permission proof under the contained user's database token. This
-- does not prove an actual MI login or permissions inherited from Entra groups.
DECLARE @Impersonating bit = 0;
BEGIN TRY
    EXECUTE AS USER = N'sidequest-app';
    SET @Impersonating = 1;
    IF COALESCE(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE TABLE'), 1) <> 0
        OR COALESCE(HAS_PERMS_BY_NAME(N'dbo', N'SCHEMA', N'ALTER'), 1) <> 0
        OR COALESCE(HAS_PERMS_BY_NAME(N'dbo', N'SCHEMA', N'CONTROL'), 1) <> 0
        OR COALESCE(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'ALTER ANY USER'), 1) <> 0
        OR COALESCE(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'ALTER ANY ROLE'), 1) <> 0
        OR COALESCE(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'IMPERSONATE ANY USER'), 1) <> 0
        THROW 51021, 'Runtime unexpectedly has DDL or principal management permission.', 1;
    IF EXISTS (SELECT 1 FROM @Tables
        WHERE COALESCE(HAS_PERMS_BY_NAME(N'dbo.' + QUOTENAME(Name), N'OBJECT', N'SELECT'), 0) <> 1
           OR COALESCE(HAS_PERMS_BY_NAME(N'dbo.' + QUOTENAME(Name), N'OBJECT', N'INSERT'), 0) <> 1
           OR COALESCE(HAS_PERMS_BY_NAME(N'dbo.' + QUOTENAME(Name), N'OBJECT', N'UPDATE'), 0) <> 1
           OR COALESCE(HAS_PERMS_BY_NAME(N'dbo.' + QUOTENAME(Name), N'OBJECT', N'DELETE'), 0) <> 1
           OR COALESCE(HAS_PERMS_BY_NAME(N'dbo.' + QUOTENAME(Name), N'OBJECT', N'CONTROL'), 1) <> 0
           OR COALESCE(HAS_PERMS_BY_NAME(N'dbo.' + QUOTENAME(Name), N'OBJECT', N'ALTER'), 1) <> 0)
        THROW 51022, 'Runtime table permissions do not match the reviewed boundary.', 1;
    IF COALESCE(HAS_PERMS_BY_NAME(N'dbo.__EFMigrationsHistory', N'OBJECT', N'SELECT'), 0) <> 1
        OR COALESCE(HAS_PERMS_BY_NAME(N'dbo.__EFMigrationsHistory', N'OBJECT', N'INSERT'), 1) <> 0
        OR COALESCE(HAS_PERMS_BY_NAME(N'dbo.__EFMigrationsHistory', N'OBJECT', N'UPDATE'), 1) <> 0
        OR COALESCE(HAS_PERMS_BY_NAME(N'dbo.__EFMigrationsHistory', N'OBJECT', N'DELETE'), 1) <> 0
        OR COALESCE(HAS_PERMS_BY_NAME(N'dbo.__EFMigrationsHistory', N'OBJECT', N'ALTER'), 1) <> 0
        OR COALESCE(HAS_PERMS_BY_NAME(N'dbo.__EFMigrationsHistory', N'OBJECT', N'CONTROL'), 1) <> 0
        THROW 51023, 'Runtime migration history permissions exceed SELECT.', 1;
    REVERT;
    SET @Impersonating = 0;
END TRY
BEGIN CATCH
    IF @Impersonating = 1 REVERT;
    THROW;
END CATCH;
