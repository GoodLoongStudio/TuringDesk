export interface RuntimeModelConfig {
  providerId: string
  mode: 'mock' | 'openai-compatible' | 'harness'
  baseUrl: string
  model: string
  credential?: string
}

export interface PublicModelConfig {
  providerId: string
  mode: RuntimeModelConfig['mode']
  baseUrl: string
  model: string
  hasCredential: boolean
}

export function publicModelConfig(config: RuntimeModelConfig): PublicModelConfig {
  return {
    providerId: config.providerId,
    mode: config.mode,
    baseUrl: config.baseUrl,
    model: config.model,
    hasCredential: Boolean(config.credential)
  }
}

export function validateModelConfig(config: RuntimeModelConfig): void {
  if (!config.providerId?.trim()) throw new Error('providerId is required')
  if (!['mock', 'openai-compatible', 'harness'].includes(config.mode)) throw new Error(`Unsupported mode: ${config.mode}`)
  if (config.mode === 'mock') return
  if (!config.model?.trim()) throw new Error('Model ID is required')
  if (config.mode === 'openai-compatible') {
    const url = new URL(config.baseUrl)
    if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new Error('Base URL must use http or https')
  }
}
