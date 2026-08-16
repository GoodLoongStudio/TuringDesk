import { createAgentGateway, type AgentGateway, type AgentTraceSink } from './harness-gateway.js'
import { publicModelConfig, type PublicModelConfig, type RuntimeModelConfig, validateModelConfig } from './model-config.js'

const COMPATIBLE_ROUTE = 'turingdesk-compatible'

export class ModelGatewayManager {
  private config: RuntimeModelConfig = {
    providerId: 'mock',
    mode: 'mock',
    baseUrl: '',
    model: ''
  }

  private gateway: AgentGateway = createAgentGateway()

  /** Effective execution mode. Every real model is mediated by Harness. */
  get mode(): 'mock' | 'harness' {
    return this.config.mode === 'mock' ? 'mock' : 'harness'
  }

  get current(): PublicModelConfig {
    return publicModelConfig(this.config)
  }

  async configure(next: RuntimeModelConfig): Promise<PublicModelConfig> {
    validateModelConfig(next)
    await this.gateway.close?.().catch(() => undefined)
    this.config = { ...next, credential: next.credential?.trim() || undefined }

    if (this.config.mode === 'mock') {
      process.env.TURINGDESK_RUNTIME_MODE = 'mock'
      clearHarnessModelEnvironment()
      this.gateway = createAgentGateway()
      await this.gateway.ensureReady?.()
      return this.current
    }

    // Harness is the one and only Agent Kernel for every non-mock provider.
    process.env.TURINGDESK_RUNTIME_MODE = 'harness'
    process.env.TURINGDESK_HARNESS_MODEL = this.config.model

    if (isDeepSeekProvider(this.config.providerId)) {
      process.env.TURINGDESK_HARNESS_PROVIDER = 'deepseek-official'
      if (this.config.credential) process.env.DEEPSEEK_API_KEY = this.config.credential
      else delete process.env.DEEPSEEK_API_KEY
      if (this.config.baseUrl) process.env.DEEPSEEK_BASE_URL = this.config.baseUrl
      else delete process.env.DEEPSEEK_BASE_URL
      delete process.env.TURINGDESK_MODEL_API_KEY
      delete process.env.TURINGDESK_PI_PROVIDER_JSON
    } else {
      process.env.TURINGDESK_HARNESS_PROVIDER = COMPATIBLE_ROUTE
      delete process.env.DEEPSEEK_API_KEY
      delete process.env.DEEPSEEK_BASE_URL

      if (this.config.credential) process.env.TURINGDESK_MODEL_API_KEY = this.config.credential
      else delete process.env.TURINGDESK_MODEL_API_KEY

      process.env.TURINGDESK_PI_PROVIDER_JSON = JSON.stringify({
        [COMPATIBLE_ROUTE]: {
          displayName: providerDisplayName(this.config.providerId),
          ...(this.config.credential ? { apiKeyEnv: 'TURINGDESK_MODEL_API_KEY' } : {}),
          api: 'openai-completions',
          baseURL: this.config.baseUrl,
          models: [{ id: this.config.model }]
        }
      })
    }

    this.gateway = createAgentGateway()
    // Applying a model configuration is not considered successful until the
    // bundled Harness process, TuringDesk profile and MCP bridge all initialize.
    await this.gateway.ensureReady?.()
    return this.current
  }

  run(message: string, onTrace?: AgentTraceSink): Promise<string> {
    return this.gateway.run(message, onTrace)
  }

  test(): Promise<string> {
    if (this.config.mode === 'mock') return Promise.resolve('Mock runtime is ready.')
    return this.gateway.run('Reply exactly with: OK')
  }

  async close(): Promise<void> {
    await this.gateway.close?.()
  }
}

function isDeepSeekProvider(providerId: string): boolean {
  return providerId === 'deepseek' || providerId === 'deepseek-harness'
}

function providerDisplayName(providerId: string): string {
  switch (providerId) {
    case 'ollama': return 'Ollama'
    case 'lmstudio': return 'LM Studio'
    case 'openai-compatible': return 'OpenAI-compatible endpoint'
    default: return providerId
  }
}

function clearHarnessModelEnvironment(): void {
  delete process.env.TURINGDESK_HARNESS_PROVIDER
  delete process.env.TURINGDESK_HARNESS_MODEL
  delete process.env.TURINGDESK_PI_PROVIDER_JSON
  delete process.env.TURINGDESK_MODEL_API_KEY
  delete process.env.DEEPSEEK_API_KEY
  delete process.env.DEEPSEEK_BASE_URL
}