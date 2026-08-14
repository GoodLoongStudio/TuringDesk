import { createAgentGateway, type AgentGateway } from './harness-gateway.js'
import { OpenAiCompatibleGateway } from './openai-compatible-gateway.js'
import { publicModelConfig, type PublicModelConfig, type RuntimeModelConfig, validateModelConfig } from './model-config.js'

export class ModelGatewayManager {
  private config: RuntimeModelConfig = {
    providerId: 'mock',
    mode: 'mock',
    baseUrl: '',
    model: ''
  }

  private gateway: AgentGateway = createAgentGateway()

  get mode(): RuntimeModelConfig['mode'] {
    return this.config.mode
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
      this.gateway = createAgentGateway()
    } else if (this.config.mode === 'openai-compatible') {
      this.gateway = new OpenAiCompatibleGateway(this.config)
    } else {
      process.env.TURINGDESK_RUNTIME_MODE = 'harness'
      const isDeepSeekHarness = this.config.providerId === 'deepseek-harness'
      process.env.TURINGDESK_HARNESS_PROVIDER = isDeepSeekHarness ? 'deepseek-official' : this.config.providerId
      process.env.TURINGDESK_HARNESS_MODEL = this.config.model

      if (isDeepSeekHarness) {
        if (this.config.credential) process.env.DEEPSEEK_API_KEY = this.config.credential
        else delete process.env.DEEPSEEK_API_KEY
        if (this.config.baseUrl) process.env.DEEPSEEK_BASE_URL = this.config.baseUrl
        else delete process.env.DEEPSEEK_BASE_URL
      }

      this.gateway = createAgentGateway()
    }

    return this.current
  }

  run(message: string): Promise<string> {
    return this.gateway.run(message)
  }

  test(): Promise<string> {
    if (this.config.mode === 'mock') return Promise.resolve('Mock runtime is ready.')
    return this.gateway.run('Reply exactly with: OK')
  }

  async close(): Promise<void> {
    await this.gateway.close?.()
  }
}
