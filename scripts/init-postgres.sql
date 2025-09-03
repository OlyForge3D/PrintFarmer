-- PostgreSQL initialization script for PrintFarmer
-- This script sets up the database with proper permissions and extensions

-- Enable required extensions
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS "pg_stat_statements";

-- Create additional schemas if needed
-- CREATE SCHEMA IF NOT EXISTS audit;
-- CREATE SCHEMA IF NOT EXISTS logs;

-- Grant permissions
GRANT ALL PRIVILEGES ON DATABASE printfarmer TO printfarmer;

-- Performance optimizations
ALTER DATABASE printfarmer SET timezone TO 'UTC';
ALTER DATABASE printfarmer SET datestyle TO 'ISO, MDY';
ALTER DATABASE printfarmer SET default_text_search_config TO 'english';

-- Log successful initialization
SELECT 'PrintFarmer PostgreSQL database initialized successfully' AS status;