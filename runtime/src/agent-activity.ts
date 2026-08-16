export type AgentPhase = 'idle' | 'running' | 'completed' | 'error'

export interface AgentRunSummary {
  id: number
  prompt: string
  phase: AgentPhase
  startedAt: string
  finishedAt?: string
  replyPreview?: string
  error?: string
}

export interface AgentActivitySnapshot {
  phase: AgentPhase
  busy: boolean
  runId: number
  currentPrompt?: string
  startedAt?: string
  finishedAt?: string
  replyPreview?: string
  error?: string
  history: AgentRunSummary[]
}

const HISTORY_LIMIT = 8
const PREVIEW_LIMIT = 280

export class AgentActivityTracker {
  private nextRunId = 1
  private phase: AgentPhase = 'idle'
  private current: AgentRunSummary | undefined
  private readonly history: AgentRunSummary[] = []

  begin(prompt: string): number {
    const run: AgentRunSummary = {
      id: this.nextRunId++,
      prompt: compact(prompt, 320),
      phase: 'running',
      startedAt: new Date().toISOString()
    }
    this.current = run
    this.phase = 'running'
    return run.id
  }

  complete(runId: number, reply: string): void {
    if (!this.current || this.current.id !== runId) return
    this.current.phase = 'completed'
    this.current.finishedAt = new Date().toISOString()
    this.current.replyPreview = compact(reply, PREVIEW_LIMIT)
    this.phase = 'completed'
    this.pushHistory(this.current)
    this.current = undefined
  }

  fail(runId: number, error: unknown): void {
    if (!this.current || this.current.id !== runId) return
    this.current.phase = 'error'
    this.current.finishedAt = new Date().toISOString()
    this.current.error = compact(error instanceof Error ? error.message : String(error), PREVIEW_LIMIT)
    this.phase = 'error'
    this.pushHistory(this.current)
    this.current = undefined
  }

  snapshot(): AgentActivitySnapshot {
    const latest = this.current ?? this.history[0]
    return {
      phase: this.current ? 'running' : this.phase,
      busy: Boolean(this.current),
      runId: latest?.id ?? 0,
      currentPrompt: this.current?.prompt,
      startedAt: latest?.startedAt,
      finishedAt: latest?.finishedAt,
      replyPreview: latest?.replyPreview,
      error: latest?.error,
      history: this.history.map((item) => ({ ...item }))
    }
  }

  private pushHistory(run: AgentRunSummary): void {
    this.history.unshift({ ...run })
    if (this.history.length > HISTORY_LIMIT) this.history.length = HISTORY_LIMIT
  }
}

function compact(value: string, limit: number): string {
  const normalized = value.replace(/\s+/g, ' ').trim()
  return normalized.length <= limit ? normalized : `${normalized.slice(0, limit - 1)}…`
}
