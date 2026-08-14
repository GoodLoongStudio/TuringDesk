import { existsSync, mkdirSync } from 'node:fs'
import { createRequire } from 'node:module'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

export interface HarnessSpawnSpec {
  command: string
  args: string[]
  cwd: string
  env: NodeJS.ProcessEnv
  profilePath: string
  binPath: string
  mcpServerPath: string
}

const moduleDir = dirname(fileURLToPath(import.meta.url))
const require = createRequire(import.meta.url)

export function resolveBundledHarnessBin(): string {
  if (process.env.TURINGDESK_HARNESS_BIN) return resolve(process.env.TURINGDESK_HARNESS_BIN)
  return require.resolve('@deepseek-ai/dsh-sdk-jsonrpc-demo/bin')
}

export function resolveTuringDeskProfile(): string {
  if (process.env.TURINGDESK_HARNESS_PROFILE) return resolve(process.env.TURINGDESK_HARNESS_PROFILE)

  const candidates = [
    resolve(moduleDir, '../harness/turingdesk.cordis.yml'), // source/dist layout
    resolve(moduleDir, 'harness/turingdesk.cordis.yml') // portable flat dist layout
  ]
  const found = candidates.find(existsSync)
  if (!found) throw new Error(`Bundled TuringDesk Harness profile was not found. Checked: ${candidates.join(', ')}`)
  return found
}

export function resolveWindowsMcpServer(): string {
  if (process.env.TURINGDESK_MCP_SERVER) return resolve(process.env.TURINGDESK_MCP_SERVER)
  const candidate = resolve(moduleDir, 'windows-mcp-server.js')
  if (!existsSync(candidate)) throw new Error(`Bundled TuringDesk MCP server was not found: ${candidate}`)
  return candidate
}

export function defaultHarnessDataRoot(): string {
  if (process.env.TURINGDESK_RUNTIME_DATA_DIR) return resolve(process.env.TURINGDESK_RUNTIME_DATA_DIR)
  if (process.platform === 'win32' && process.env.LOCALAPPDATA) {
    return join(process.env.LOCALAPPDATA, 'TuringDesk', 'Runtime')
  }
  return resolve(process.cwd(), '.turingdesk-runtime')
}

export function createBundledHarnessSpawnSpec(): HarnessSpawnSpec {
  const profilePath = resolveTuringDeskProfile()
  const binPath = resolveBundledHarnessBin()
  const mcpServerPath = resolveWindowsMcpServer()
  const dataRoot = defaultHarnessDataRoot()
  const sessionRoot = join(dataRoot, 'sessions')
  mkdirSync(sessionRoot, { recursive: true })

  const explicitCommand = process.env.TURINGDESK_HARNESS_COMMAND?.trim()
  const command = explicitCommand || process.execPath
  const args = explicitCommand
    ? parseStringArray(process.env.TURINGDESK_HARNESS_ARGS)
    : [binPath]

  const env: NodeJS.ProcessEnv = {
    ...process.env,
    DSH_CORDIS_CONFIG: profilePath,
    DSH_CWD: resolve(process.env.TURINGDESK_AGENT_CWD ?? process.cwd()),
    DSH_SESSION_ROOT: process.env.DSH_SESSION_ROOT ?? sessionRoot,
    TURINGDESK_MCP_NODE: process.execPath,
    TURINGDESK_MCP_SERVER: mcpServerPath,
    TURINGDESK_CAPABILITY_URL: process.env.TURINGDESK_CAPABILITY_URL ?? 'http://127.0.0.1:4318'
  }

  return {
    command,
    args,
    cwd: process.env.TURINGDESK_HARNESS_PROCESS_CWD ?? dirname(profilePath),
    env,
    profilePath,
    binPath,
    mcpServerPath
  }
}

function parseStringArray(value: string | undefined): string[] {
  if (!value) return []
  const parsed: unknown = JSON.parse(value)
  if (!Array.isArray(parsed) || !parsed.every((item) => typeof item === 'string')) {
    throw new Error('TURINGDESK_HARNESS_ARGS must be a JSON array of strings')
  }
  return parsed
}
