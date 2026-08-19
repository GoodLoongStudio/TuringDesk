import { Server } from '@modelcontextprotocol/sdk/server/index.js'
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js'
import { CallToolRequestSchema, ListToolsRequestSchema, type Tool } from '@modelcontextprotocol/sdk/types.js'
import { CapabilityClient } from './capability-client.js'

const capabilities = new CapabilityClient()
const server = new Server(
  { name: 'turingdesk-windows', version: '0.4.0' },
  { capabilities: { tools: {} } }
)

type ToolExecutionClass = 'instant' | 'interactive' | 'agentic'
type ToolRoutingMetadata = {
  executionClass: ToolExecutionClass
  stateless: boolean
  expectedLatencyMs: string
  requiresAgentPlanning: boolean
}
type ClassifiedTool = Tool & {
  _meta: {
    turingdesk: ToolRoutingMetadata
  }
}

function routed(tool: Tool, routing: ToolRoutingMetadata): ClassifiedTool {
  return {
    ...tool,
    _meta: {
      turingdesk: routing
    }
  }
}

const instantRead = (expectedLatencyMs = '<50'): ToolRoutingMetadata => ({
  executionClass: 'instant',
  stateless: true,
  expectedLatencyMs,
  requiresAgentPlanning: false
})

const interactive = (expectedLatencyMs = '<250'): ToolRoutingMetadata => ({
  executionClass: 'interactive',
  stateless: true,
  expectedLatencyMs,
  requiresAgentPlanning: false
})

const tools: ClassifiedTool[] = [
  routed({
    name: 'desktop_snapshot',
    description: 'Read one coherent snapshot of the current Windows desktop: monitors, visible application windows and the foreground window. Use this before multi-window desktop planning when current screen state matters.',
    inputSchema: { type: 'object', additionalProperties: false, properties: {} }
  }, instantRead('<80')),
  routed({
    name: 'app_launch',
    description: 'Launch an allow-listed Windows desktop application. Allowed app aliases: chrome, code, terminal.',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      properties: {
        app: { type: 'string', enum: ['chrome', 'code', 'terminal'], description: 'Application alias to launch.' }
      },
      required: ['app']
    }
  }, interactive()),
  routed({
    name: 'window_list',
    description: 'List visible top-level Windows application windows. TuringDesk itself is excluded.',
    inputSchema: { type: 'object', additionalProperties: false, properties: {} }
  }, instantRead()),
  routed({
    name: 'window_find',
    description: 'Find the first visible top-level window whose title or process name contains a query.',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      properties: { query: { type: 'string', description: 'Window title or process-name fragment.' } },
      required: ['query']
    }
  }, instantRead()),
  routed({
    name: 'window_focus',
    description: 'Restore and focus a visible top-level window by handle returned by window_list/window_find.',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      properties: { handle: { type: 'string', description: 'Opaque window handle returned by TuringDesk.' } },
      required: ['handle']
    }
  }, interactive()),
  routed({
    name: 'window_move',
    description: 'Move a visible top-level window. Coordinates are clamped to the current work area.',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      properties: {
        handle: { type: 'string' },
        x: { type: 'integer' },
        y: { type: 'integer' }
      },
      required: ['handle', 'x', 'y']
    }
  }, interactive()),
  routed({
    name: 'window_resize',
    description: 'Resize a visible top-level window. Size is clamped to safe minimums and the work area.',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      properties: {
        handle: { type: 'string' },
        width: { type: 'integer' },
        height: { type: 'integer' }
      },
      required: ['handle', 'width', 'height']
    }
  }, interactive()),
  routed({
    name: 'window_tile',
    description: 'Tile two visible top-level windows side by side using handles returned by window_list/window_find.',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      properties: {
        leftHandle: { type: 'string' },
        rightHandle: { type: 'string' }
      },
      required: ['leftHandle', 'rightHandle']
    }
  }, interactive('<350'))
]

server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools }))

server.setRequestHandler(CallToolRequestSchema, async request => {
  const name = request.params.name
  const args = isRecord(request.params.arguments) ? request.params.arguments : {}

  try {
    let result: unknown
    switch (name) {
      case 'desktop_snapshot':
        result = await capabilities.execute('desktop.snapshot')
        break
      case 'app_launch':
        result = await capabilities.execute('app.launch', { app: requireString(args, 'app') })
        break
      case 'window_list':
        result = await capabilities.execute('window.list')
        break
      case 'window_find':
        result = await capabilities.execute('window.find', { query: requireString(args, 'query') })
        break
      case 'window_focus':
        result = await capabilities.execute('window.focus', { handle: requireString(args, 'handle') })
        break
      case 'window_move':
        result = await capabilities.execute('window.move', {
          handle: requireString(args, 'handle'),
          x: requireInteger(args, 'x'),
          y: requireInteger(args, 'y')
        })
        break
      case 'window_resize':
        result = await capabilities.execute('window.resize', {
          handle: requireString(args, 'handle'),
          width: requireInteger(args, 'width'),
          height: requireInteger(args, 'height')
        })
        break
      case 'window_tile':
        result = await capabilities.execute('window.tile', {
          leftHandle: requireString(args, 'leftHandle'),
          rightHandle: requireString(args, 'rightHandle')
        })
        break
      default:
        throw new Error(`Unknown MCP tool: ${name}`)
    }

    return { content: [{ type: 'text' as const, text: JSON.stringify(result) }] }
  } catch (error) {
    return {
      content: [{ type: 'text' as const, text: error instanceof Error ? error.message : String(error) }],
      isError: true
    }
  }
})

function requireString(args: Record<string, unknown>, name: string): string {
  const value = args[name]
  if (typeof value !== 'string' || value.trim().length === 0) throw new Error(`${name} must be a non-empty string`)
  return value
}

function requireInteger(args: Record<string, unknown>, name: string): number {
  const value = args[name]
  if (typeof value !== 'number' || !Number.isInteger(value)) throw new Error(`${name} must be an integer`)
  return value
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

const transport = new StdioServerTransport()
await server.connect(transport)
