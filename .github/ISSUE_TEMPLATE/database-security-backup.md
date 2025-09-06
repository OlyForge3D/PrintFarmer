# Database Security and Automated Backup System

## Summary
Implement comprehensive database security measures and automated backup/recovery system for PrintFarmer production environments, including encryption, access control, backup automation, disaster recovery, and compliance features.

## Background
PrintFarmer's current database implementation lacks critical production security and backup features:
- No data encryption at rest or in transit
- Basic authentication without role-based access control
- No automated backup system
- No disaster recovery procedures
- No audit logging for database operations
- No compliance features for data protection regulations

This creates significant risks for production deployments including:
- Data breaches and unauthorized access
- Data loss without recovery options
- Compliance violations (GDPR, HIPAA, etc.)
- Extended downtime during failures
- No audit trail for security investigations

## Requirements

### 1. Database Security Hardening
- **Encryption at rest** for all database files and backups
- **Encryption in transit** with TLS 1.2+ for all connections
- **Database user management** with role-based access control
- **Connection pooling** with secure credential management
- **Database firewall** rules and network isolation
- **SQL injection prevention** with parameterized queries
- **Database vulnerability scanning** and patch management

### 2. Authentication and Authorization
- **Service account management** with principle of least privilege
- **Application user segregation** for different services
- **Read-only user accounts** for reporting and monitoring
- **Database administrator accounts** with multi-factor authentication
- **Connection string encryption** in configuration files
- **Credential rotation** automation
- **Database session monitoring** and timeout enforcement

### 3. Automated Backup System
- **Full database backups** with configurable schedules
- **Incremental backups** for large databases
- **Point-in-time recovery** capabilities
- **Cross-region backup replication** for disaster recovery
- **Backup integrity verification** and corruption detection
- **Automated backup retention** policies
- **Backup encryption** with separate key management

### 4. Disaster Recovery and High Availability
- **Database replication** for high availability
- **Automatic failover** with health monitoring
- **Recovery Time Objective (RTO)** < 1 hour
- **Recovery Point Objective (RPO)** < 15 minutes
- **Disaster recovery testing** automation
- **Database cluster management** for scalability
- **Backup restoration testing** procedures

### 5. Audit Logging and Compliance
- **Database audit trails** for all DDL and DML operations
- **User access logging** with detailed session information
- **Data modification tracking** for sensitive tables
- **Compliance reporting** for GDPR, HIPAA, SOX requirements
- **Log retention policies** with secure archival
- **Audit log protection** from tampering
- **Automated compliance checks** and alerts

### 6. Performance and Optimization
- **Database performance monitoring** with query analysis
- **Index optimization** and maintenance automation
- **Connection pool tuning** for optimal resource usage
- **Query performance baselines** and regression detection
- **Database statistics** collection and analysis
- **Automatic maintenance** window scheduling
- **Storage optimization** and space management

## Technical Implementation

### 1. Database Security Configuration

#### PostgreSQL Security Hardening
```sql
-- Create application-specific roles
CREATE ROLE printfarmer_app WITH LOGIN ENCRYPTED PASSWORD 'secure_password';
CREATE ROLE printfarmer_readonly WITH LOGIN ENCRYPTED PASSWORD 'readonly_password';
CREATE ROLE printfarmer_backup WITH LOGIN ENCRYPTED PASSWORD 'backup_password';

-- Grant minimal required permissions
GRANT CONNECT ON DATABASE printfarmer TO printfarmer_app;
GRANT USAGE ON SCHEMA public TO printfarmer_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO printfarmer_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO printfarmer_app;

-- Read-only access for monitoring
GRANT CONNECT ON DATABASE printfarmer TO printfarmer_readonly;
GRANT USAGE ON SCHEMA public TO printfarmer_readonly;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO printfarmer_readonly;

-- Backup user permissions
GRANT CONNECT ON DATABASE printfarmer TO printfarmer_backup;
ALTER ROLE printfarmer_backup WITH REPLICATION;
```

#### Database Connection Configuration
```csharp
// Secure connection string with encryption
public class DatabaseConfiguration
{
    public string GetConnectionString(string provider, IConfiguration config)
    {
        var connectionString = provider switch
        {
            "postgres" => $"Host={config["DB_HOST"]};Database={config["DB_NAME"]};" +
                         $"Username={config["DB_USER"]};Password={config["DB_PASSWORD"]};" +
                         $"SslMode=Require;Trust Server Certificate=false;" +
                         $"Include Error Detail=true;Pooling=true;MinPoolSize=5;MaxPoolSize=100;",
            
            "sqlserver" => $"Server={config["DB_HOST"]};Database={config["DB_NAME"]};" +
                          $"User Id={config["DB_USER"]};Password={config["DB_PASSWORD"]};" +
                          $"Encrypt=True;TrustServerCertificate=False;" +
                          $"Connection Timeout=30;Command Timeout=300;",
            
            _ => throw new NotSupportedException($"Database provider '{provider}' not supported")
        };

        return connectionString;
    }
}
```

### 2. Automated Backup System

#### Backup Service Implementation
```csharp
public class DatabaseBackupService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseBackupService> _logger;
    private readonly string _backupDirectory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformBackup();
                await CleanupOldBackups();
                
                // Schedule next backup based on configuration
                var nextBackup = GetNextBackupTime();
                var delay = nextBackup - DateTime.UtcNow;
                await Task.Delay(delay, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backup process failed");
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
        }
    }

    private async Task PerformBackup()
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var backupFileName = $"printfarmer_backup_{timestamp}";
        
        var provider = _configuration["DB_PROVIDER"];
        
        switch (provider)
        {
            case "postgres":
                await PerformPostgreSQLBackup(backupFileName);
                break;
            case "sqlserver":
                await PerformSQLServerBackup(backupFileName);
                break;
            default:
                throw new NotSupportedException($"Backup not supported for {provider}");
        }

        await VerifyBackupIntegrity(backupFileName);
        await EncryptBackup(backupFileName);
        await UploadToCloudStorage(backupFileName);
    }

    private async Task PerformPostgreSQLBackup(string fileName)
    {
        var connectionString = _configuration.GetConnectionString("Postgres");
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        
        var backupPath = Path.Combine(_backupDirectory, $"{fileName}.sql");
        
        var startInfo = new ProcessStartInfo
        {
            FileName = "pg_dump",
            Arguments = $"-h {builder.Host} -p {builder.Port} -U {builder.Username} " +
                       $"-d {builder.Database} -f {backupPath} --verbose --clean --if-exists",
            Environment = { ["PGPASSWORD"] = builder.Password },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo);
        await process.WaitForExitAsync();
        
        if (process.ExitCode != 0)
        {
            throw new Exception($"pg_dump failed with exit code {process.ExitCode}");
        }

        _logger.LogInformation("PostgreSQL backup completed: {BackupPath}", backupPath);
    }
}
```

#### Docker Backup Container
```dockerfile
# Dockerfile.backup
FROM postgres:15-alpine

# Install additional tools
RUN apk add --no-cache \
    aws-cli \
    gnupg \
    curl \
    gzip

# Copy backup scripts
COPY scripts/backup/ /usr/local/bin/

# Set up cron for scheduled backups
RUN echo "0 2 * * * /usr/local/bin/backup-database.sh" > /var/spool/cron/crontabs/root

ENTRYPOINT ["crond", "-f", "-l", "2"]
```

### 3. High Availability Configuration

#### PostgreSQL Master-Slave Replication
```yaml
# docker-compose.ha.yml
services:
  postgres-master:
    image: postgres:15
    environment:
      POSTGRES_REPLICATION_USER: replicator
      POSTGRES_REPLICATION_PASSWORD: ${REPLICATION_PASSWORD}
    volumes:
      - postgres_master_data:/var/lib/postgresql/data
      - ./postgres/master/postgresql.conf:/etc/postgresql/postgresql.conf
      - ./postgres/master/pg_hba.conf:/etc/postgresql/pg_hba.conf
    command: postgres -c config_file=/etc/postgresql/postgresql.conf

  postgres-slave:
    image: postgres:15
    environment:
      PGUSER: replicator
      POSTGRES_PASSWORD: ${REPLICATION_PASSWORD}
      POSTGRES_MASTER_SERVICE: postgres-master
    volumes:
      - postgres_slave_data:/var/lib/postgresql/data
      - ./postgres/slave/recovery.conf:/var/lib/postgresql/data/recovery.conf
    depends_on:
      - postgres-master

  postgres-failover:
    image: citusdata/pg_auto_failover:latest
    environment:
      PGDATA: /var/lib/postgresql/data
      PG_AUTOCTL_MONITOR_HOSTNAME: postgres-failover
    volumes:
      - postgres_monitor_data:/var/lib/postgresql/data

volumes:
  postgres_master_data:
  postgres_slave_data:
  postgres_monitor_data:
```

#### Database Health Monitoring
```csharp
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDbConnection _connection;
    private readonly ILogger<DatabaseHealthCheck> _logger;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 5;
            
            var startTime = DateTime.UtcNow;
            await command.ExecuteScalarAsync(cancellationToken);
            var duration = DateTime.UtcNow - startTime;
            
            var data = new Dictionary<string, object>
            {
                ["response_time_ms"] = duration.TotalMilliseconds,
                ["connection_state"] = _connection.State.ToString()
            };

            return duration.TotalSeconds < 5 
                ? HealthCheckResult.Healthy("Database is healthy", data)
                : HealthCheckResult.Degraded("Database response time is slow", data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database health check failed");
            return HealthCheckResult.Unhealthy("Database is not accessible", ex);
        }
    }
}
```

### 4. Audit Logging Implementation

#### Database Audit Configuration
```sql
-- PostgreSQL audit logging setup
CREATE EXTENSION IF NOT EXISTS pgaudit;

-- Configure audit logging
ALTER SYSTEM SET shared_preload_libraries = 'pgaudit';
ALTER SYSTEM SET pgaudit.log = 'ddl,write,role';
ALTER SYSTEM SET pgaudit.log_catalog = 'off';
ALTER SYSTEM SET pgaudit.log_parameter = 'on';
ALTER SYSTEM SET pgaudit.log_statement_once = 'on';

-- Create audit table for application events
CREATE TABLE audit_log (
    id BIGSERIAL PRIMARY KEY,
    timestamp TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    user_id VARCHAR(255),
    action VARCHAR(100) NOT NULL,
    table_name VARCHAR(100),
    record_id VARCHAR(100),
    old_values JSONB,
    new_values JSONB,
    ip_address INET,
    user_agent TEXT
);

-- Create audit trigger function
CREATE OR REPLACE FUNCTION audit_trigger()
RETURNS TRIGGER AS $$
BEGIN
    INSERT INTO audit_log (action, table_name, record_id, old_values, new_values)
    VALUES (
        TG_OP,
        TG_TABLE_NAME,
        COALESCE(NEW.id::TEXT, OLD.id::TEXT),
        CASE WHEN TG_OP IN ('UPDATE', 'DELETE') THEN row_to_json(OLD) END,
        CASE WHEN TG_OP IN ('INSERT', UPDATE') THEN row_to_json(NEW) END
    );
    
    RETURN COALESCE(NEW, OLD);
END;
$$ LANGUAGE plpgsql;
```

#### Application-Level Audit Service
```csharp
public class AuditService : IAuditService
{
    private readonly AppDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public async Task LogAsync(AuditEvent auditEvent)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        
        var log = new AuditLog
        {
            Timestamp = DateTime.UtcNow,
            UserId = httpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            Action = auditEvent.Action,
            TableName = auditEvent.TableName,
            RecordId = auditEvent.RecordId,
            OldValues = auditEvent.OldValues,
            NewValues = auditEvent.NewValues,
            IpAddress = httpContext?.Connection?.RemoteIpAddress?.ToString(),
            UserAgent = httpContext?.Request?.Headers["User-Agent"].ToString()
        };

        _context.AuditLogs.Add(log);
        await _context.SaveChangesAsync();
    }

    public async Task<IEnumerable<AuditLog>> GetAuditTrailAsync(
        string tableName = null,
        string recordId = null,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        var query = _context.AuditLogs.AsQueryable();

        if (!string.IsNullOrEmpty(tableName))
            query = query.Where(a => a.TableName == tableName);

        if (!string.IsNullOrEmpty(recordId))
            query = query.Where(a => a.RecordId == recordId);

        if (fromDate.HasValue)
            query = query.Where(a => a.Timestamp >= fromDate.Value);

        if (toDate.HasValue)
            query = query.Where(a => a.Timestamp <= toDate.Value);

        return await query.OrderByDescending(a => a.Timestamp).ToListAsync();
    }
}
```

### 5. Backup Verification and Testing

#### Backup Integrity Verification
```bash
#!/bin/bash
# scripts/verify-backup.sh

set -e

BACKUP_FILE=$1
TEMP_DB="printfarmer_backup_test_$(date +%s)"

if [ -z "$BACKUP_FILE" ]; then
    echo "Usage: $0 <backup_file>"
    exit 1
fi

echo "Verifying backup integrity for: $BACKUP_FILE"

# Create temporary database
createdb "$TEMP_DB"

# Restore backup to temporary database
if [[ "$BACKUP_FILE" == *.sql ]]; then
    psql "$TEMP_DB" < "$BACKUP_FILE"
elif [[ "$BACKUP_FILE" == *.dump ]]; then
    pg_restore -d "$TEMP_DB" "$BACKUP_FILE"
else
    echo "Unsupported backup format"
    dropdb "$TEMP_DB"
    exit 1
fi

# Run integrity checks
echo "Running integrity checks..."

# Check table counts
TABLE_COUNT=$(psql -t -c "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='public';" "$TEMP_DB")
echo "Tables restored: $TABLE_COUNT"

# Check for critical tables
CRITICAL_TABLES=("printers" "users" "print_jobs")
for table in "${CRITICAL_TABLES[@]}"; do
    EXISTS=$(psql -t -c "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name='$table');" "$TEMP_DB")
    if [[ "$EXISTS" == *"t"* ]]; then
        echo "✓ Critical table '$table' exists"
    else
        echo "✗ Critical table '$table' missing"
        dropdb "$TEMP_DB"
        exit 1
    fi
done

# Check data integrity
RECORD_COUNT=$(psql -t -c "SELECT COUNT(*) FROM printers;" "$TEMP_DB" 2>/dev/null || echo "0")
echo "Printer records: $RECORD_COUNT"

# Cleanup
dropdb "$TEMP_DB"
echo "Backup verification completed successfully!"
```

### 6. Environment-Specific Configuration

#### Production Database Configuration
```yaml
# .env.production
# Database Security
DB_PROVIDER=postgres
DB_HOST=postgres-primary.internal
DB_NAME=printfarmer
DB_USER=printfarmer_app
DB_PASSWORD_FILE=/run/secrets/db_password
DB_SSL_MODE=require
DB_SSL_CERT=/etc/ssl/certs/postgres-client.crt
DB_SSL_KEY=/etc/ssl/private/postgres-client.key
DB_SSL_ROOT_CERT=/etc/ssl/certs/postgres-ca.crt

# Connection Pool Settings
DB_MIN_POOL_SIZE=10
DB_MAX_POOL_SIZE=100
DB_CONNECTION_TIMEOUT=30
DB_COMMAND_TIMEOUT=300

# Backup Configuration
BACKUP_SCHEDULE="0 2 * * *"  # Daily at 2 AM
BACKUP_RETENTION_DAYS=30
BACKUP_ENCRYPTION_KEY_FILE=/run/secrets/backup_encryption_key
BACKUP_S3_BUCKET=printfarmer-backups
BACKUP_S3_REGION=us-east-1

# Audit Logging
ENABLE_AUDIT_LOGGING=true
AUDIT_LOG_SENSITIVE_DATA=false
AUDIT_LOG_RETENTION_DAYS=365

# High Availability
ENABLE_READ_REPLICAS=true
READ_REPLICA_HOSTS=postgres-read-01.internal,postgres-read-02.internal
ENABLE_AUTO_FAILOVER=true
FAILOVER_TIMEOUT_SECONDS=30
```

## Acceptance Criteria

### 1. Database Security
- [ ] All database connections use TLS 1.2+ encryption
- [ ] Database files are encrypted at rest
- [ ] Role-based access control is implemented for all database users
- [ ] Connection strings are encrypted in configuration
- [ ] Database firewall rules restrict access to authorized IPs only
- [ ] SQL injection vulnerability testing passes 100%

### 2. Authentication and Authorization
- [ ] Application uses dedicated service accounts with minimal privileges
- [ ] Read-only accounts are used for monitoring and reporting
- [ ] Database administrator accounts require multi-factor authentication
- [ ] Credential rotation works without service interruption
- [ ] Database sessions have appropriate timeout enforcement
- [ ] Connection pooling is optimized and secure

### 3. Backup System
- [ ] Automated backups run according to configured schedule
- [ ] Backup integrity verification passes 100% of the time
- [ ] Point-in-time recovery works for any timestamp within retention period
- [ ] Cross-region backup replication is functioning
- [ ] Backup restoration completes within RTO (1 hour)
- [ ] Backup encryption and decryption work correctly

### 4. Disaster Recovery
- [ ] Database replication maintains < 15 minute RPO
- [ ] Automatic failover completes within 1 minute
- [ ] Disaster recovery testing passes monthly
- [ ] Database cluster scaling works under load
- [ ] Recovery procedures are documented and tested
- [ ] Business continuity is maintained during failover

### 5. Audit and Compliance
- [ ] All database operations are logged with full audit trail
- [ ] User access and session information is tracked
- [ ] Sensitive data changes are logged with before/after values
- [ ] Compliance reports can be generated for GDPR/HIPAA
- [ ] Audit logs are protected from unauthorized modification
- [ ] Log retention policies are enforced automatically

### 6. Performance and Monitoring
- [ ] Database performance metrics are collected and monitored
- [ ] Query analysis identifies optimization opportunities
- [ ] Connection pool metrics show optimal resource usage
- [ ] Performance regressions are detected automatically
- [ ] Maintenance windows are scheduled and automated
- [ ] Storage usage is monitored with growth predictions

## Testing Requirements

### Security Testing
- [ ] **Penetration testing** of database access controls
- [ ] **Encryption verification** for data at rest and in transit
- [ ] **SQL injection testing** across all application endpoints
- [ ] **Access control testing** for all user roles
- [ ] **Vulnerability scanning** of database servers
- [ ] **Network security testing** of database connections

### Backup and Recovery Testing
- [ ] **Full backup and restore** testing monthly
- [ ] **Point-in-time recovery** testing quarterly
- [ ] **Cross-region restore** testing annually
- [ ] **Backup corruption** detection and handling
- [ ] **Large database** backup performance testing
- [ ] **Automated restoration** workflow verification

### High Availability Testing
- [ ] **Planned failover** testing monthly
- [ ] **Unplanned failover** simulation quarterly
- [ ] **Split-brain scenario** handling
- [ ] **Network partition** recovery testing
- [ ] **Performance under failover** load testing
- [ ] **Data consistency** verification after failover

### Compliance Testing
- [ ] **GDPR compliance** audit trail verification
- [ ] **Data retention** policy enforcement testing
- [ ] **Right to be forgotten** implementation testing
- [ ] **Data export** functionality testing
- [ ] **Audit log** integrity verification
- [ ] **Compliance report** generation testing

## Documentation Requirements

### Operations Documentation
- [ ] **Database security** configuration guide
- [ ] **Backup and recovery** procedures manual
- [ ] **High availability** setup and maintenance guide
- [ ] **Disaster recovery** playbooks and procedures
- [ ] **Performance tuning** and optimization guide
- [ ] **Troubleshooting** guide for common database issues

### Compliance Documentation
- [ ] **Audit logging** configuration and usage guide
- [ ] **Data governance** policies and procedures
- [ ] **Privacy protection** implementation details
- [ ] **Compliance reporting** procedures
- [ ] **Data retention** and deletion policies
- [ ] **Security incident** response procedures

### Developer Documentation
- [ ] **Database schema** documentation and ER diagrams
- [ ] **Secure coding** practices for database interactions
- [ ] **Connection management** best practices
- [ ] **Query optimization** guidelines
- [ ] **Audit logging** implementation guide
- [ ] **Database testing** strategies and tools

## Implementation Phases

### Phase 1: Security Hardening (2 weeks)
- Database encryption at rest and in transit
- Role-based access control implementation
- Secure connection string management
- Network security and firewall configuration

### Phase 2: Backup Automation (2 weeks)
- Automated backup service implementation
- Backup verification and integrity checking
- Cross-region replication setup
- Backup encryption and key management

### Phase 3: High Availability (2 weeks)
- Database replication configuration
- Automatic failover implementation
- Health monitoring and alerting
- Load balancing and connection management

### Phase 4: Audit and Compliance (1 week)
- Audit logging implementation
- Compliance reporting automation
- Data governance policy enforcement
- Security event monitoring

### Phase 5: Testing and Optimization (1 week)
- Comprehensive testing of all systems
- Performance optimization and tuning
- Documentation completion
- Training and knowledge transfer

## Success Metrics

### Security Metrics
- **Zero security vulnerabilities** in database layer
- **100% encryption compliance** for data at rest and in transit
- **Sub-5 second** authentication response times
- **Zero unauthorized access** attempts successful
- **100% audit trail** coverage for sensitive operations

### Backup and Recovery Metrics
- **99.9% backup success rate** over 12-month period
- **RTO < 1 hour** for database restoration
- **RPO < 15 minutes** for data recovery
- **100% backup integrity** verification success rate
- **Zero data loss** incidents over 12-month period

### Performance and Availability Metrics
- **99.99% database uptime** (excluding planned maintenance)
- **<100ms average** database query response time
- **Automatic failover** completion in <60 seconds
- **Connection pool efficiency** >90% utilization
- **Storage growth prediction** accuracy >95%

## Dependencies

### External Dependencies
- Database server software (PostgreSQL, SQL Server, etc.)
- Backup storage infrastructure (local, cloud)
- Encryption key management system
- Monitoring and alerting platforms
- Compliance and audit tools

### Internal Dependencies
- Authentication and authorization system (#34)
- Monitoring and observability infrastructure (#50)
- Security hardening implementation (#49)
- Network configuration and firewalls
- Container orchestration and secrets management

## Risk Mitigation

### Data Protection Risks
- Multiple backup copies in different locations
- Regular backup verification and testing procedures
- Encryption key backup and recovery procedures
- Data corruption detection and prevention
- Regulatory compliance monitoring and reporting

### Operational Risks
- Automated monitoring and alerting for all critical systems
- Well-documented procedures for common scenarios
- Regular disaster recovery testing and updates
- Performance monitoring and capacity planning
- Staff training and knowledge documentation

---

## Related Issues
- Security Hardening and HTTPS Configuration (#49)
- Production Monitoring and Observability Infrastructure (#50)
- Authentication and Authorization System (#34)
- DevOps Automation and CI/CD Pipeline (#TBD)

## References
- [PostgreSQL Security Documentation](https://www.postgresql.org/docs/current/security.html)
- [SQL Server Security Best Practices](https://docs.microsoft.com/en-us/sql/relational-databases/security/)
- [GDPR Compliance Guidelines](https://gdpr.eu/compliance/)
- [OWASP Database Security Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Database_Security_Cheat_Sheet.html)
- [NIST Cybersecurity Framework](https://www.nist.gov/cybersecurity)