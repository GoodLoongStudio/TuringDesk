export interface CapabilityEnvelope<T> {
  ok: boolean
  result: T | null
  error: string | null
}

export interface WindowSummary {
  handle: string
  title: string
  processId: number
  processName: string
  x: number
  y: number
  width: number
  height: number
}

export class CapabilityClient {
  private readonly baseUrl: string

  constructor(baseUrl = process.env.TURINGDESK_CAPABILITY_URL ?? 'http://127.0.0.1:4318') {
    this.baseUrl = baseUrl.replace(/\/$/, '')
  }

  async execute<T>(name: string, args: Record<string, unknown> = {}, signal?: AbortSignal): Promise<T> {
    const response = await fetch(`${this.baseUrl}/v1/capabilities/execute`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ name, arguments: args }),
      signal
    })

    const envelope = await response.json() as CapabilityEnvelope<T>
    if (!response.ok || !envelope.ok) {
      throw new Error(envelope.error || `Capability ${name} failed with HTTP ${response.status}`)
    }

    return envelope.result as T
  }

  async waitForWindow(query: string, timeoutMs = 8_000): Promise<WindowSummary | null> {
    const deadline = Date.now() + timeoutMs
    while (Date.now() < deadline) {
      const window = await this.execute<WindowSummary | null>('window.find', { query })
      if (window) return window
      await sleep(250)
    }
    return null
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms))
}
