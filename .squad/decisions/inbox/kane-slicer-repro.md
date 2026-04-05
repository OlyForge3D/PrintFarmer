# Slicer UI Hidden in Microservices Mode — Root Cause & Test Gap

**Author:** Kane (QA)
**Date:** 2026-04-04
**Status:** Bug identified, regression tests added, fix landed in controller

## Root Cause

Program.cs:101 uses DEPLOYMENT_MODE != "microservices" as a single slicerEnabled flag for both module loading AND capability reporting. In Docker microservices mode, this forces Slicer:Enabled="False" into IConfiguration, which SystemCapabilitiesController reads and returns slicingEnabled: false to the frontend. Frontend Layout.tsx:321 hides all requiresSlicingCapability nav items.

## Fix Applied

SystemCapabilitiesController now overrides slicerEnabled=true when DEPLOYMENT_MODE=microservices on non-ARM. This is correct because the slicer-host container provides the capability remotely.

## Regression Tests Added

9 tests in SystemCapabilitiesIntegrationTests.cs covering both standard and microservices modes, including unauthenticated access and feature independence.
