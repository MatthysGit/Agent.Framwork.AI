# Ai.AgentFramwork.Massar.Web.Tests

This is a pure unit-test xUnit project for the Massar AI Agent Framework.

## Coverage included

- Router hard-guard coverage for:
  - DocumentEditAgent
  - ExcelAnalyticsAgent
  - ExecutiveInsightAgent
  - DataIntelligenceAgent
  - ForecastingAgent
  - AnomalyDetectionAgent
  - DataSegmentationAgent
  - WhatIfSimulationAgent
  - DocumentSearchAgent
- Agent model selection and persistence behavior
- ChatPipeline compensation policy guard
- ChatPipeline private helper behavior that drives routing/edit inference
- Chat message window safety filtering
- ChatSession state handling

## Recommended next step after this drop-in

Add a second integration test project for AdventureWorks2025-backed scenarios:
- SQLAgent live query behavior
- Data retrieval assumptions for forecasting/anomaly/segmentation
- end-to-end business test prompts against the real schema
