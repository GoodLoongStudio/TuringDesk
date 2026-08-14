import type { AgentGateway } from './harness-gateway.js'
import type { RuntimeModelConfig } from './model-config.js'

export class OpenAiCompatibleGateway implements AgentGateway {
  readonly mode = 'openai-compatible'

  constructor(private readonly config: RuntimeModelConfig) {}

  async run(message: string): Promise<string> {
    const endpoint = chatEndpoint(this.config.baseUrl)
    const headers: Record<string, string> = { 'content-type': 'application/json' }
    if (this.config.credential) headers.authorization = `Bearer ${this.config.credential}`

    const controller = new AbortController()
    const timer = setTimeout(() => controller.abort(), 120_000)
    try {
      const response = await fetch(endpoint, {
        method: 'POST',
        headers,
        body: JSON.stringify({
          model: this.config.model,
          messages: [{ role: 'user', content: message }],
          stream: false
        }),
        signal: controller.signal
      })

      const raw = await response.text()
      if (!response.ok) throw new Error(`Model request failed (${response.status}): ${preview(raw)}`)

      const parsed = JSON.parse(raw) as unknown
      const text = assistantText(parsed)
      if (!text) throw new Error('Model returned no assistant text')
      return text
    } finally {
      clearTimeout(timer)
    }
  }
}

function chatEndpoint(baseUrl: string): string {
  const base = baseUrl.replace(/\/+$/, '')
  return base.endsWith('/chat/completions') ? base : `${base}/chat/completions`
}

function assistantText(value: unknown): string {
  if (!isRecord(value) || !Array.isArray(value.choices) || value.choices.length === 0) return ''
  const first = value.choices[0]
  if (!isRecord(first) || !isRecord(first.message)) return ''
  const content = first.message.content
  if (typeof content === 'string') return content.trim()
  if (!Array.isArray(content)) return ''

  return content
    .filter(item => isRecord(item) && typeof item.text === 'string')
    .map(item => String(item.text))
    .join('')
    .trim()
}

function preview(value: string): string {
  return value.replace(/\s+/g, ' ').slice(0, 400)
}

function isRecord(value: unknown): value is Record<string, any> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}
