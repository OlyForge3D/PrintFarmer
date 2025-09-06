# DevOps Automation and CI/CD Pipeline Implementation

## Summary
Implement comprehensive DevOps automation including CI/CD pipelines, automated testing, deployment automation, infrastructure as code, and operational monitoring to enable reliable and efficient software delivery for PrintFarmer.

## Background
PrintFarmer currently lacks automated DevOps practices essential for production software delivery:
- No CI/CD pipeline for automated builds and deployments
- Manual testing and deployment processes
- No infrastructure as code (IaC) for environment consistency
- No automated security scanning or vulnerability management
- Limited deployment rollback and blue-green deployment capabilities
- No automated environment provisioning or scaling

This creates operational risks including:
- Inconsistent deployments across environments
- Human error in manual processes
- Slow time-to-market for features and fixes
- Difficulty in scaling operations
- No standardized security and compliance checks

## Requirements

### 1. CI/CD Pipeline Architecture
- **Multi-stage pipeline** (build, test, security scan, deploy)
- **Branch-based workflows** with feature branch automation
- **Automated testing integration** (unit, integration, E2E tests)
- **Artifact management** with versioning and security scanning
- **Environment promotion** (dev → staging → production)
- **Deployment gates** with approval workflows
- **Rollback automation** for failed deployments

### 2. Build and Test Automation
- **Automated builds** triggered by code commits
- **Parallel test execution** for faster feedback
- **Code quality gates** with SonarQube or similar
- **Security vulnerability scanning** of dependencies
- **Docker image building** with security scanning
- **Performance regression testing** automation
- **Test result reporting** and trend analysis

### 3. Infrastructure as Code (IaC)
- **Terraform/ARM templates** for cloud infrastructure
- **Docker Compose** for local and staging environments
- **Kubernetes manifests** for container orchestration
- **Environment configuration** management
- **Network and security** rule automation
- **Database schema** migration automation
- **Secrets management** integration

### 4. Deployment Automation
- **Blue-green deployments** for zero-downtime updates
- **Canary releases** with automated rollback
- **Database migration** automation with rollback capability
- **Configuration management** with environment-specific values
- **Health check integration** for deployment validation
- **Load balancer** integration for traffic management
- **Post-deployment verification** automation

### 5. Security and Compliance Automation
- **Static Application Security Testing (SAST)** integration
- **Dynamic Application Security Testing (DAST)** automation
- **Dependency vulnerability scanning** with automated updates
- **Container image scanning** for security vulnerabilities
- **Infrastructure security scanning** with compliance checks
- **Secrets scanning** to prevent credential leaks
- **Compliance reporting** automation (GDPR, SOX, etc.)

### 6. Monitoring and Observability Integration
- **Deployment metrics** tracking and analysis
- **Application performance monitoring** integration
- **Error tracking** and automated alerting
- **Log aggregation** from deployment pipelines
- **Business metrics** tracking post-deployment
- **SLA monitoring** and compliance reporting
- **Capacity planning** with automated scaling triggers

## Technical Implementation

### 1. GitHub Actions CI/CD Pipeline

#### Main Pipeline Workflow
```yaml
# .github/workflows/ci-cd.yml
name: CI/CD Pipeline

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main ]

env:
  REGISTRY: ghcr.io
  IMAGE_NAME: ${{ github.repository }}

jobs:
  test:
    runs-on: ubuntu-latest
    strategy:
      matrix:
        dotnet-version: ['9.0.x']
        node-version: ['18.x', '20.x']
    
    steps:
    - uses: actions/checkout@v4
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: ${{ matrix.dotnet-version }}
    
    - name: Setup Node.js
      uses: actions/setup-node@v4
      with:
        node-version: ${{ matrix.node-version }}
    
    - name: Cache dependencies
      uses: actions/cache@v3
      with:
        path: |
          ~/.nuget/packages
          ~/.npm
        key: ${{ runner.os }}-nuget-npm-${{ hashFiles('**/*.csproj', '**/package-lock.json') }}
    
    - name: Restore .NET dependencies
      run: dotnet restore src/farm-web.sln
    
    - name: Install npm dependencies
      run: |
        cd src/Web/ReactApp
        npm ci
    
    - name: Run .NET tests
      run: dotnet test src/farm-web.sln --configuration Release --logger trx --results-directory "TestResults"
    
    - name: Run React tests
      run: |
        cd src/Web/ReactApp
        npm run test:ci
    
    - name: Upload test results
      uses: actions/upload-artifact@v3
      if: always()
      with:
        name: test-results
        path: TestResults

  security-scan:
    runs-on: ubuntu-latest
    needs: test
    
    steps:
    - uses: actions/checkout@v4
    
    - name: Run Trivy vulnerability scanner
      uses: aquasecurity/trivy-action@master
      with:
        scan-type: 'fs'
        scan-ref: '.'
        format: 'sarif'
        output: 'trivy-results.sarif'
    
    - name: Upload Trivy scan results
      uses: github/codeql-action/upload-sarif@v2
      with:
        sarif_file: 'trivy-results.sarif'
    
    - name: Run CodeQL Analysis
      uses: github/codeql-action/analyze@v2
      with:
        languages: csharp, javascript

  build-and-push:
    runs-on: ubuntu-latest
    needs: [test, security-scan]
    permissions:
      contents: read
      packages: write
    
    steps:
    - uses: actions/checkout@v4
    
    - name: Log in to Container Registry
      uses: docker/login-action@v3
      with:
        registry: ${{ env.REGISTRY }}
        username: ${{ github.actor }}
        password: ${{ secrets.GITHUB_TOKEN }}
    
    - name: Extract metadata
      id: meta
      uses: docker/metadata-action@v5
      with:
        images: ${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}
        tags: |
          type=ref,event=branch
          type=ref,event=pr
          type=sha
          type=raw,value=latest,enable={{is_default_branch}}
    
    - name: Build and push Docker images
      uses: docker/build-push-action@v5
      with:
        context: .
        file: ./Dockerfile.react
        push: true
        tags: ${{ steps.meta.outputs.tags }}
        labels: ${{ steps.meta.outputs.labels }}
        cache-from: type=gha
        cache-to: type=gha,mode=max

  deploy-staging:
    runs-on: ubuntu-latest
    needs: build-and-push
    if: github.ref == 'refs/heads/develop'
    environment: staging
    
    steps:
    - uses: actions/checkout@v4
    
    - name: Deploy to staging
      run: |
        echo "Deploying to staging environment"
        # Add actual deployment commands here
    
    - name: Run smoke tests
      run: |
        echo "Running smoke tests on staging"
        # Add smoke test commands here

  deploy-production:
    runs-on: ubuntu-latest
    needs: build-and-push
    if: github.ref == 'refs/heads/main'
    environment: production
    
    steps:
    - uses: actions/checkout@v4
    
    - name: Deploy to production
      run: |
        echo "Deploying to production environment"
        # Add actual deployment commands here
    
    - name: Run health checks
      run: |
        echo "Running production health checks"
        # Add health check commands here
```

### 2. Infrastructure as Code

#### Terraform Configuration
```hcl
# infrastructure/main.tf
terraform {
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.0"
    }
  }
  
  backend "s3" {
    bucket = "printfarmer-terraform-state"
    key    = "production/terraform.tfstate"
    region = "us-east-1"
  }
}

# VPC and Networking
module "vpc" {
  source = "terraform-aws-modules/vpc/aws"
  
  name = "printfarmer-vpc"
  cidr = "10.0.0.0/16"
  
  azs             = ["us-east-1a", "us-east-1b", "us-east-1c"]
  private_subnets = ["10.0.1.0/24", "10.0.2.0/24", "10.0.3.0/24"]
  public_subnets  = ["10.0.101.0/24", "10.0.102.0/24", "10.0.103.0/24"]
  
  enable_nat_gateway = true
  enable_vpn_gateway = true
}

# EKS Cluster
module "eks" {
  source = "terraform-aws-modules/eks/aws"
  
  cluster_name    = "printfarmer-cluster"
  cluster_version = "1.28"
  
  vpc_id     = module.vpc.vpc_id
  subnet_ids = module.vpc.private_subnets
  
  eks_managed_node_groups = {
    main = {
      desired_size = 3
      max_size     = 10
      min_size     = 1
      
      instance_types = ["t3.medium"]
      
      k8s_labels = {
        Environment = "production"
        Application = "printfarmer"
      }
    }
  }
}

# RDS Database
resource "aws_db_instance" "postgres" {
  identifier = "printfarmer-db"
  
  engine         = "postgres"
  engine_version = "15.4"
  instance_class = "db.t3.micro"
  
  allocated_storage     = 20
  max_allocated_storage = 100
  storage_encrypted     = true
  
  db_name  = "printfarmer"
  username = "postgres"
  password = var.db_password
  
  vpc_security_group_ids = [aws_security_group.rds.id]
  db_subnet_group_name   = aws_db_subnet_group.main.name
  
  backup_retention_period = 7
  backup_window          = "03:00-04:00"
  maintenance_window     = "sun:04:00-sun:05:00"
  
  skip_final_snapshot = false
  deletion_protection = true
  
  tags = {
    Name        = "printfarmer-db"
    Environment = "production"
  }
}
```

#### Kubernetes Deployment Manifests
```yaml
# k8s/api-deployment.yml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: printfarmer-api
  labels:
    app: printfarmer-api
spec:
  replicas: 3
  selector:
    matchLabels:
      app: printfarmer-api
  template:
    metadata:
      labels:
        app: printfarmer-api
    spec:
      containers:
      - name: api
        image: ghcr.io/jpapiez/printfarmer:latest
        ports:
        - containerPort: 8080
        env:
        - name: ConnectionStrings__Postgres
          valueFrom:
            secretKeyRef:
              name: printfarmer-secrets
              key: database-connection-string
        - name: ASPNETCORE_ENVIRONMENT
          value: "Production"
        livenessProbe:
          httpGet:
            path: /healthz
            port: 8080
          initialDelaySeconds: 30
          periodSeconds: 10
        readinessProbe:
          httpGet:
            path: /ready
            port: 8080
          initialDelaySeconds: 5
          periodSeconds: 5
        resources:
          requests:
            memory: "256Mi"
            cpu: "250m"
          limits:
            memory: "512Mi"
            cpu: "500m"
---
apiVersion: v1
kind: Service
metadata:
  name: printfarmer-api-service
spec:
  selector:
    app: printfarmer-api
  ports:
  - port: 80
    targetPort: 8080
  type: ClusterIP
```

### 3. Deployment Automation Scripts

#### Blue-Green Deployment Script
```bash
#!/bin/bash
# scripts/deploy-blue-green.sh

set -e

NAMESPACE=${NAMESPACE:-production}
APP_NAME=${APP_NAME:-printfarmer-api}
IMAGE_TAG=${IMAGE_TAG:-latest}
HEALTH_CHECK_URL=${HEALTH_CHECK_URL:-/healthz}
TIMEOUT=${TIMEOUT:-300}

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log() {
    echo -e "${GREEN}[$(date +'%Y-%m-%d %H:%M:%S')] $1${NC}"
}

warn() {
    echo -e "${YELLOW}[$(date +'%Y-%m-%d %H:%M:%S')] WARNING: $1${NC}"
}

error() {
    echo -e "${RED}[$(date +'%Y-%m-%d %H:%M:%S')] ERROR: $1${NC}"
    exit 1
}

# Determine current active environment
CURRENT_COLOR=$(kubectl get service ${APP_NAME}-service -n $NAMESPACE -o jsonpath='{.spec.selector.color}' 2>/dev/null || echo "blue")
if [ "$CURRENT_COLOR" = "blue" ]; then
    DEPLOY_COLOR="green"
else
    DEPLOY_COLOR="blue"
fi

log "Current active environment: $CURRENT_COLOR"
log "Deploying to: $DEPLOY_COLOR"

# Deploy to inactive environment
log "Updating deployment $APP_NAME-$DEPLOY_COLOR"
kubectl set image deployment/$APP_NAME-$DEPLOY_COLOR \
    api=ghcr.io/jpapiez/printfarmer:$IMAGE_TAG \
    -n $NAMESPACE

# Wait for rollout to complete
log "Waiting for rollout to complete..."
kubectl rollout status deployment/$APP_NAME-$DEPLOY_COLOR -n $NAMESPACE --timeout=${TIMEOUT}s

# Get the service URL for health checks
SERVICE_URL="http://$(kubectl get service ${APP_NAME}-${DEPLOY_COLOR}-service -n $NAMESPACE -o jsonpath='{.status.loadBalancer.ingress[0].hostname}')"

# Health check with retry
log "Performing health checks on $DEPLOY_COLOR environment"
for i in {1..30}; do
    if curl -f -s "$SERVICE_URL$HEALTH_CHECK_URL" > /dev/null; then
        log "Health check passed on attempt $i"
        break
    else
        warn "Health check failed on attempt $i, retrying in 10 seconds..."
        sleep 10
    fi
    
    if [ $i -eq 30 ]; then
        error "Health check failed after 30 attempts, rolling back deployment"
    fi
done

# Run smoke tests
log "Running smoke tests on $DEPLOY_COLOR environment"
if ! ./scripts/smoke-tests.sh "$SERVICE_URL"; then
    error "Smoke tests failed, rolling back deployment"
fi

# Switch traffic to new environment
log "Switching traffic to $DEPLOY_COLOR environment"
kubectl patch service ${APP_NAME}-service -n $NAMESPACE -p '{"spec":{"selector":{"color":"'$DEPLOY_COLOR'"}}}'

# Verify traffic switch
sleep 10
log "Verifying traffic switch"
for i in {1..10}; do
    if curl -f -s "http://$(kubectl get service ${APP_NAME}-service -n $NAMESPACE -o jsonpath='{.status.loadBalancer.ingress[0].hostname}')$HEALTH_CHECK_URL" > /dev/null; then
        log "Traffic switch verified on attempt $i"
        break
    else
        warn "Traffic verification failed on attempt $i, retrying in 5 seconds..."
        sleep 5
    fi
done

log "Deployment completed successfully!"
log "Active environment is now: $DEPLOY_COLOR"

# Optional: Scale down old environment after successful deployment
read -p "Scale down $CURRENT_COLOR environment? (y/N): " -r
if [[ $REPLY =~ ^[Yy]$ ]]; then
    kubectl scale deployment $APP_NAME-$CURRENT_COLOR --replicas=1 -n $NAMESPACE
    log "Scaled down $CURRENT_COLOR environment to 1 replica"
fi
```

### 4. Automated Testing Framework

#### End-to-End Test Pipeline
```bash
#!/bin/bash
# scripts/run-e2e-tests.sh

set -e

BASE_URL=${BASE_URL:-http://localhost:8080}
TEST_TIMEOUT=${TEST_TIMEOUT:-300}

log() {
    echo -e "\033[0;32m[$(date +'%Y-%m-%d %H:%M:%S')] $1\033[0m"
}

error() {
    echo -e "\033[0;31m[$(date +'%Y-%m-%d %H:%M:%S')] ERROR: $1\033[0m"
    exit 1
}

# Wait for application to be ready
log "Waiting for application to be ready at $BASE_URL"
timeout $TEST_TIMEOUT bash -c "
    while ! curl -f -s $BASE_URL/healthz > /dev/null; do
        echo 'Waiting for application...'
        sleep 5
    done
"

# Run API tests
log "Running API integration tests"
cd src/tests/Farm.Web.Api.Tests
dotnet test --configuration Release --logger "trx;LogFileName=api-tests.trx" || error "API tests failed"

# Run React E2E tests
log "Running React E2E tests"
cd ../../Web/ReactApp
npm run test:e2e || error "E2E tests failed"

# Run performance tests
log "Running performance tests"
npx lighthouse $BASE_URL --output=json --output-path=lighthouse-report.json --chrome-flags="--headless"

# Validate performance metrics
PERFORMANCE_SCORE=$(node -p "JSON.parse(require('fs').readFileSync('lighthouse-report.json')).categories.performance.score * 100")
if (( $(echo "$PERFORMANCE_SCORE < 80" | bc -l) )); then
    error "Performance score ($PERFORMANCE_SCORE) below threshold (80)"
fi

log "All tests passed successfully!"
log "Performance score: $PERFORMANCE_SCORE/100"
```

### 5. Security Scanning Integration

#### Security Pipeline
```yaml
# .github/workflows/security.yml
name: Security Scanning

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main ]
  schedule:
    - cron: '0 2 * * 1' # Weekly scan on Monday at 2 AM

jobs:
  dependency-scan:
    runs-on: ubuntu-latest
    steps:
    - uses: actions/checkout@v4
    
    - name: Run Snyk to check for vulnerabilities
      uses: snyk/actions/dotnet@master
      env:
        SNYK_TOKEN: ${{ secrets.SNYK_TOKEN }}
      with:
        args: --severity-threshold=high
    
    - name: Upload Snyk report
      uses: github/codeql-action/upload-sarif@v2
      if: always()
      with:
        sarif_file: snyk.sarif

  container-scan:
    runs-on: ubuntu-latest
    steps:
    - uses: actions/checkout@v4
    
    - name: Build Docker image
      run: docker build -t printfarmer-scan:latest -f Dockerfile.react .
    
    - name: Run Trivy vulnerability scanner
      uses: aquasecurity/trivy-action@master
      with:
        image-ref: 'printfarmer-scan:latest'
        format: 'sarif'
        output: 'trivy-results.sarif'
    
    - name: Upload Trivy scan results
      uses: github/codeql-action/upload-sarif@v2
      if: always()
      with:
        sarif_file: 'trivy-results.sarif'

  secrets-scan:
    runs-on: ubuntu-latest
    steps:
    - uses: actions/checkout@v4
      with:
        fetch-depth: 0
    
    - name: Run TruffleHog OSS
      uses: trufflesecurity/trufflehog@main
      with:
        path: ./
        base: main
        head: HEAD
        extra_args: --debug --only-verified
```

## Acceptance Criteria

### 1. CI/CD Pipeline
- [ ] Automated builds trigger on all code commits
- [ ] All tests (unit, integration, E2E) pass before deployment
- [ ] Security scans complete without high-severity findings
- [ ] Deployment artifacts are versioned and stored securely
- [ ] Pipeline failures trigger immediate notifications
- [ ] Deployment rollback works within 5 minutes

### 2. Infrastructure as Code
- [ ] All infrastructure is defined as code in version control
- [ ] Environment provisioning is fully automated
- [ ] Infrastructure changes are peer-reviewed
- [ ] Environment consistency is maintained across dev/staging/prod
- [ ] Infrastructure secrets are managed securely
- [ ] Disaster recovery environments can be provisioned automatically

### 3. Deployment Automation
- [ ] Zero-downtime deployments work for all components
- [ ] Database migrations are automated with rollback capability
- [ ] Health checks validate deployment success
- [ ] Traffic switching is automated and monitored
- [ ] Rollback procedures complete within defined RTO
- [ ] Deployment notifications are sent to relevant teams

### 4. Security Integration
- [ ] Security scans are integrated into CI/CD pipeline
- [ ] High-severity vulnerabilities block deployments
- [ ] Dependency updates are automated for security patches
- [ ] Container images are scanned before deployment
- [ ] Secrets are never committed to version control
- [ ] Compliance checks are automated and reported

### 5. Monitoring and Observability
- [ ] Deployment metrics are tracked and analyzed
- [ ] Performance regressions are detected automatically
- [ ] Error rates and response times are monitored
- [ ] Business metrics are tracked post-deployment
- [ ] SLA compliance is monitored and reported
- [ ] Alerts are sent for deployment-related issues

### 6. Operational Excellence
- [ ] Runbooks are automated where possible
- [ ] Documentation is automatically updated
- [ ] Knowledge sharing is embedded in processes
- [ ] Disaster recovery procedures are tested regularly
- [ ] Capacity planning is data-driven and automated
- [ ] Continuous improvement feedback loops are established

## Implementation Phases

### Phase 1: Basic CI/CD (2 weeks)
- GitHub Actions pipeline setup
- Automated build and test execution
- Basic deployment to staging environment
- Container registry integration

### Phase 2: Infrastructure as Code (2 weeks)
- Terraform/CloudFormation templates
- Environment provisioning automation
- Network and security configuration
- Database infrastructure automation

### Phase 3: Advanced Deployment (2 weeks)
- Blue-green deployment implementation
- Canary release capabilities
- Database migration automation
- Health check integration

### Phase 4: Security and Compliance (1 week)
- Security scanning integration
- Vulnerability management automation
- Compliance reporting automation
- Secrets management implementation

### Phase 5: Monitoring and Optimization (1 week)
- Pipeline monitoring and alerting
- Performance optimization
- Documentation and training
- Process refinement and improvement

## Success Metrics

### Deployment Metrics
- **Deployment frequency** increased to multiple times per day
- **Lead time** from commit to production < 2 hours
- **Deployment failure rate** < 5% of all deployments
- **Mean time to recovery** < 30 minutes for deployment issues
- **Change failure rate** < 10% requiring immediate fixes

### Quality Metrics
- **Automated test coverage** > 80% for all code changes
- **Security vulnerability resolution** < 24 hours for critical issues
- **Infrastructure drift detection** and remediation < 1 hour
- **Compliance check pass rate** 100% for all deployments
- **Documentation currency** 100% for all automated processes

### Operational Metrics
- **Pipeline availability** > 99.5% uptime
- **Build success rate** > 95% on first attempt
- **Environment provisioning time** < 15 minutes for full stack
- **Rollback success rate** 100% when initiated
- **Team productivity** measured by feature delivery velocity

## Dependencies

### External Dependencies
- GitHub Actions or equivalent CI/CD platform
- Container registry (GitHub Container Registry, ECR, etc.)
- Cloud infrastructure provider (AWS, Azure, GCP)
- Terraform or equivalent IaC tool
- Security scanning tools (Snyk, Trivy, CodeQL)

### Internal Dependencies
- Security hardening implementation (#49)
- Database backup and recovery system (#51)
- Monitoring and observability infrastructure (#50)
- Authentication and authorization system (#34)
- Code quality and testing standards

## Risk Mitigation

### Deployment Risks
- Comprehensive testing at every stage
- Automated rollback procedures
- Blue-green deployments for zero downtime
- Canary releases for risk mitigation
- Extensive monitoring and alerting

### Security Risks
- Integrated security scanning in pipeline
- Secrets management best practices
- Regular vulnerability assessments
- Compliance automation and reporting
- Incident response procedures

### Operational Risks
- Comprehensive documentation and runbooks
- Cross-training and knowledge sharing
- Disaster recovery testing
- Capacity planning and scaling automation
- Continuous improvement processes

---

## Related Issues
- Security Hardening and HTTPS Configuration (#49)
- Production Monitoring and Observability Infrastructure (#50)
- Database Security and Automated Backup System (#51)
- Authentication and Authorization System (#34)

## References
- [GitHub Actions Documentation](https://docs.github.com/en/actions)
- [Terraform Documentation](https://www.terraform.io/docs)
- [Kubernetes Documentation](https://kubernetes.io/docs/)
- [Docker Security Best Practices](https://docs.docker.com/develop/security-best-practices/)
- [DevOps Best Practices](https://docs.microsoft.com/en-us/azure/architecture/checklist/dev-ops)