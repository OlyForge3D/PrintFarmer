# Slicer Microservices Architecture

## Context

PrintFarmer's slicer microservices architecture enables distributed processing of 3D model files into printer-ready G-code. This system supports multiple slicer engines (OrcaSlicer, PrusaSlicer, etc.) running as independent workers, coordinated through a central orchestrator with Redis-based job queuing and real-time progress updates via SignalR.

## Business Requirements

- **Scalability**: Support horizontal scaling of slicer workers to handle varying workloads
- **Reliability**: Ensure job persistence and recovery in case of worker failures  
- **Multi-Engine Support**: Enable different slicer engines (OrcaSlicer, PrusaSlicer, Cura) simultaneously
- **Real-Time Feedback**: Provide live progress updates to users during slicing operations
- **Resource Efficiency**: Optimize CPU and memory usage across distributed workers
- **Storage Management**: Handle large model files and generated G-code efficiently

## Architecture Overview

The slicer microservices architecture consists of five core components:

```mermaid
graph TB
    Client[React Client] -->|HTTP REST| Gateway[API Gateway]
    Gateway -->|HTTP| Dispatcher[Slicer Orchestrator]
    Dispatcher -->|Enqueue/Dequeue| Queue[(Redis Queue)]
    Dispatcher -->|Store/Retrieve| Storage[(File Storage)]
    
    Worker1[OrcaSlicer Worker]
    Worker2[PrusaSlicer Worker]
    WorkerN[Worker N]
    
    Queue -->|Dequeue Jobs| Worker1
    Queue -->|Dequeue Jobs| Worker2  
    Queue -->|Dequeue Jobs| WorkerN
    
    Worker1 -->|Progress Updates| Gateway
    Worker2 -->|Progress Updates| Gateway
    WorkerN -->|Progress Updates| Gateway
    
    Gateway -->|SignalR| Client
    
    Worker1 -.->|Read/Write| Storage
    Worker2 -.->|Read/Write| Storage
    WorkerN -.->|Read/Write| Storage

    subgraph "PrintFarmer API"
        Gateway
        Dispatcher
    end

    subgraph "Infrastructure"
        Queue
        Storage
    end

    subgraph "Worker Pool"
        Worker1
        Worker2
        WorkerN
    end
```

### Component Responsibilities

1. **API Gateway (ASP.NET Core API)**
   - Expose REST endpoints for job submission and status queries
   - Handle authentication and authorization
   - Manage SignalR hubs for real-time notifications

2. **Slicer Orchestrator (Dispatcher)**
   - Coordinate job lifecycle from submission to completion
   - Validate job requests and model files
   - Manage worker health and capacity
   - Implement retry logic and error handling

3. **Redis Queue**
   - Persist job state and metadata
   - Provide priority-based job scheduling
   - Enable atomic job dequeuing for workers
   - Support job timeout and retry mechanisms

4. **File Storage (Local/Cloud)**
   - Store input model files and output G-code
   - Manage temporary working directories for workers
   - Provide secure file access with expiring URLs

5. **Slicer Workers**
   - Execute actual slicing operations using specific engines
   - Report progress and status updates
   - Handle worker registration and health checks
   - Support graceful shutdown and job handover

## Deployment Architecture

### Development Environment
```mermaid
graph LR
    Dev[Developer Machine] --> API[API + Orchestrator]
    API --> Redis[(Redis Local)]
    API --> FS[(Local Storage)]
    API --> Worker[Local Worker Pool]
```

### Production Environment  
```mermaid
graph TB
    LB[Load Balancer] --> API1[API Instance 1]
    LB --> API2[API Instance 2]
    
    API1 --> Redis[(Redis Cluster)]
    API2 --> Redis
    
    API1 --> Storage[(Distributed Storage)]
    API2 --> Storage
    
    Redis --> WP1[Worker Pool 1]
    Redis --> WP2[Worker Pool 2]
    Redis --> WPN[Worker Pool N]
    
    WP1 -.->|R/W| Storage
    WP2 -.->|R/W| Storage
    WPN -.->|R/W| Storage

    subgraph "Kubernetes Cluster"
        API1
        API2
        WP1
        WP2
        WPN
    end
```

## Sequence Diagrams

### Job Submission Flow
```mermaid
sequenceDiagram
    participant Client
    participant Gateway as API Gateway
    participant Orchestrator as Slicer Orchestrator
    participant Queue as Redis Queue
    participant Storage as File Storage
    participant Worker as Slicer Worker

    Client->>Gateway: POST /api/slicer/jobs
    Gateway->>Orchestrator: SubmitJobAsync()
    Orchestrator->>Orchestrator: ValidateRequest()
    Orchestrator->>Storage: StoreModelFile()
    Storage-->>Orchestrator: FileUrl
    Orchestrator->>Queue: EnqueueAsync()
    Queue-->>Orchestrator: JobQueued
    Orchestrator-->>Gateway: SlicingJobResponse
    Gateway-->>Client: 202 Accepted + JobId

    Note over Worker: Worker polls queue
    Worker->>Queue: DequeueAsync()
    Queue-->>Worker: DistributedSlicingJob
    Worker->>Storage: DownloadFile()
    Storage-->>Worker: ModelFileStream
    Worker->>Worker: SliceAsync()
    
    loop Progress Updates
        Worker->>Gateway: Progress Update (SignalR)
        Gateway->>Client: Progress Notification
    end

    Worker->>Storage: UploadGcode()
    Storage-->>Worker: GcodeUrl
    Worker->>Queue: CompleteJobAsync()
    Worker->>Gateway: Job Completed (SignalR)
    Gateway->>Client: Completion Notification
```

### Worker Health Check Flow
```mermaid
sequenceDiagram
    participant Orchestrator
    participant Worker1 as Worker 1
    participant Worker2 as Worker 2
    participant Queue as Redis Queue

    Orchestrator->>Worker1: IsHealthyAsync()
    Worker1-->>Orchestrator: Healthy
    Orchestrator->>Worker2: IsHealthyAsync()
    Worker2--X Orchestrator: Timeout/Error
    
    Orchestrator->>Queue: GetActiveJobs(Worker2)
    Queue-->>Orchestrator: ActiveJobs[]
    
    loop For each active job
        Orchestrator->>Queue: RequeueJob()
    end
    
    Orchestrator->>Orchestrator: RemoveWorker(Worker2)
    
    Note over Orchestrator: Worker2 marked as unhealthy
    Note over Orchestrator: Jobs redistributed to healthy workers
```

### Error Handling Flow
```mermaid
sequenceDiagram
    participant Worker
    participant Queue as Redis Queue
    participant Orchestrator
    participant Client

    Worker->>Worker: SliceAsync() throws exception
    Worker->>Queue: FailJobAsync()
    Worker->>Orchestrator: Error Notification
    
    Orchestrator->>Orchestrator: Check retry policy
    
    alt Retries available
        Orchestrator->>Queue: RequeueJob()
        Note over Queue: Job marked for retry
    else Max retries exceeded  
        Orchestrator->>Queue: MarkJobFailed()
        Orchestrator->>Client: Failure Notification (SignalR)
    end
```

## Key Design Decisions

See individual ADRs for detailed decision rationale:

- **[Queue Technology](queue-choice.md)**: Redis selected for persistence, performance, and operational simplicity
- **[Storage Strategy](storage-choice.md)**: Local file system with cloud storage abstraction for flexibility  
- **[Communication Protocol](comms-protocol.md)**: HTTP REST + SignalR for request/response and real-time updates

## Scalability Characteristics

### Horizontal Scaling
- **API Instances**: Stateless design enables load balancing across multiple API instances
- **Worker Pool**: Independent workers can be added/removed dynamically based on queue depth
- **Redis Clustering**: Supports Redis Cluster for high availability and performance
- **Storage**: File storage can scale independently using distributed storage solutions

### Performance Metrics
- **Queue Throughput**: Redis supports 100K+ operations/second
- **File Upload/Download**: Limited by network bandwidth and storage I/O
- **Worker Efficiency**: CPU-bound slicing operations scale linearly with worker count
- **Real-time Updates**: SignalR backplane supports thousands of concurrent connections

## Monitoring and Observability

### Health Checks
```csharp
// Orchestrator health endpoint returns:
{
  "isHealthy": true,
  "jobQueueHealthy": true, 
  "fileStorageHealthy": true,
  "engines": {
    "OrcaSlicer": {
      "isHealthy": true,
      "activeWorkers": 3,
      "queueDepth": 12,
      "avgProcessingTime": "00:05:30"
    }
  }
}
```

### Key Metrics
- **Queue Depth**: Number of pending jobs per slicer type
- **Worker Utilization**: Active workers vs. total capacity
- **Job Success Rate**: Completed vs. failed jobs over time
- **Processing Times**: Average and P95 latency by file size/complexity
- **Storage Usage**: Disk space utilization and cleanup efficiency

### Distributed Tracing
- Correlation IDs track jobs across all components
- OpenTelemetry integration for request tracing
- Centralized logging with structured JSON format

## Security Considerations

### Authentication & Authorization
- JWT-based authentication for API access
- Role-based authorization for job submission
- Worker authentication via shared secrets or certificates

### File Security
- Temporary signed URLs for file access
- Automatic cleanup of expired files
- Virus scanning integration for uploaded models
- Input validation and size limits

### Network Security  
- TLS encryption for all HTTP communications
- Redis AUTH for queue access
- Network segmentation between components
- Rate limiting on job submission endpoints

## Operational Procedures

### Deployment
1. Deploy Redis cluster with persistence enabled
2. Set up file storage with appropriate permissions
3. Deploy API instances behind load balancer
4. Scale worker pools based on expected load
5. Configure monitoring and alerting

### Maintenance
- **Queue Cleanup**: Automated removal of completed jobs older than 30 days
- **Storage Cleanup**: Cleanup of orphaned files and temporary directories  
- **Worker Updates**: Rolling deployment of worker updates without job loss
- **Scaling**: Auto-scaling based on queue depth and worker utilization

### Disaster Recovery
- **Redis Backup**: Point-in-time recovery with RDB snapshots
- **File Backup**: Regular backup of critical model files and configurations
- **Job Recovery**: Automatic requeuing of in-progress jobs after failures
- **Cross-Region**: Support for multi-region deployment for disaster recovery

## Future Enhancements

### Short Term (Next 6 months)
- **Auto-scaling**: Dynamic worker scaling based on queue metrics
- **Advanced Scheduling**: Priority queues and resource-aware job assignment
- **Batch Processing**: Support for bulk slicing operations
- **Enhanced Monitoring**: Custom Grafana dashboards and alerting rules

### Long Term (6-18 months)  
- **Multi-Tenancy**: Isolated slicing environments per user/organization
- **ML Integration**: Intelligent slicing parameter optimization
- **Edge Computing**: Distributed slicing on user-premises hardware
- **Advanced Storage**: Integration with object storage (S3, Azure Blob)

## References

- [Redis Documentation](https://redis.io/documentation)
- [SignalR Documentation](https://docs.microsoft.com/en-us/aspnet/signalr/)
- [ASP.NET Core Architecture](https://docs.microsoft.com/en-us/aspnet/core/)
- [Microservices Patterns](https://microservices.io/patterns/)
- [PrintFarmer Deployment Architectures](../../DEPLOYMENT_ARCHITECTURES.md)