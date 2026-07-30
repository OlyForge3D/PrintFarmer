import type { SquadConfig } from '@bradygaster/squad';

/**
 * Squad Configuration for PFarm1
 * 
 */
const config: SquadConfig = {
  version: '1.0.0',
  
  models: {
    defaultModel: 'claude-sonnet-4.5',
    defaultTier: 'standard',
    fallbackChains: {
      premium: ['gpt-5.6-sol', 'claude-opus-4.8', 'claude-opus-4.7', 'claude-sonnet-5'],
      standard: ['claude-sonnet-5', 'gpt-5.6-terra', 'gpt-5.5', 'claude-sonnet-4.6'],
      fast: ['gpt-5.6-luna', 'gemini-3.5-flash', 'claude-haiku-4.5', 'gpt-5.4-mini']
    },
    preferSameProvider: true,
    respectTierCeiling: true,
    nuclearFallback: {
      enabled: false,
      model: 'claude-haiku-4.5',
      maxRetriesBeforeNuclear: 3
    }
  },
  
  routing: {
    rules: [
      {
        workType: 'feature-dev',
        agents: ['@scribe'],
        confidence: 'high'
      },
      {
        workType: 'bug-fix',
        agents: ['@scribe'],
        confidence: 'high'
      },
      {
        workType: 'testing',
        agents: ['@scribe'],
        confidence: 'high'
      },
      {
        workType: 'documentation',
        agents: ['@scribe'],
        confidence: 'high'
      }
    ],
    governance: {
      eagerByDefault: true,
      scribeAutoRuns: false,
      allowRecursiveSpawn: false
    }
  },
  
  casting: {
    allowlistUniverses: [
      'The Usual Suspects',
      'Breaking Bad',
      'The Wire',
      'Firefly'
    ],
    overflowStrategy: 'generic',
    universeCapacity: {}
  },
  
  platforms: {
    vscode: {
      disableModelSelection: false,
      scribeMode: 'sync'
    }
  }
};

export default config;
