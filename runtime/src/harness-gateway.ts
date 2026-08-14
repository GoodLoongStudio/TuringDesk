export interface AgentGateway {
  readonly mode: string
  run(message: string): Promise<string>
  close?(): Promise<void>
}

class MockGateway implements AgentGateway {
  readonly mode = 'mock'

  async run(message: string): Promise<string> {
    return `TuringDesk v0.1 received: “${message}”. Native app/window commands are handled by the Windows desktop now; DeepSeek Harness tool wiring is the next milestone.`
  }
}

class DeepSeekHarnessGateway implements AgentGateway {
  readonly mode = 'harness'
  private harness: any | undefined

  private async getHarness(): Promise<any> {
    if (this.harness) return this.harness

    // Keep the SDK behind a dynamic boundary so the rest of TuringDesk does not
    // compile against Harness internals. The dependency is optional in v0.1.
    const dynamicImport = new Function('specifier', 'return import(specifier)') as (specifier: string) => Promise<any>
    const sdk = await dynamicImport('@deepseek-ai/dsh-sdk-client')

    const command = process.env.TURINGDESK_HARNESS_COMMAND
    if (!command) throw new Error('TURINGDESK_HARNESS_COMMAND is required in harness mode')

    let args: string[] = []
    if (process.env.TURINGDESK_HARNESS_ARGS) {
      const parsed = JSON.parse(process.env.TURINGDESK_HARNESS_ARGS)
      if (!Array.isArray(parsed) || !parsed.every((x) => typeof x === 'string')) {
        throw new Error('TURINGDESK_HARNESS_ARGS must be a JSON array of strings')
      }
      args = parsed
    }

    this.harness = new sdk.DeepSeekHarness({
      launch: { command, args },
      provider: process.env.TURINGDESK_HARNESS_PROVIDER ?? 'deepseek-official',
      model: process.env.TURINGDESK_HARNESS_MODEL ?? 'deepseek-v4-flash',
      maxTokens: Number(process.env.TURINGDESK_HARNESS_MAX_TOKENS ?? '8192')
    })

    return this.harness
  }

  async run(message: string): Promise<string> {
    const harness = await this.getHarness()
    const result = await harness.run(message)
    return result.finalResponse || '(Harness completed without a final text response.)'
  }

  async close(): Promise<void> {
    if (this.harness?.close) await this.harness.close()
  }
}

export function createAgentGateway(): AgentGateway {
  return process.env.TURINGDESK_RUNTIME_MODE?.toLowerCase() === 'harness'
    ? new DeepSeekHarnessGateway()
    : new MockGateway()
}
