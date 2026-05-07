export const DEFAULT_CODING_AGENT = 'codex';

export const CODING_AGENTS = Object.freeze([
  Object.freeze({
    id: 'claude',
    label: 'Claude Code',
    title: 'Claude Code Windows via Ollama, dialogue non interactif',
    connected: true,
  }),
  Object.freeze({
    id: 'codex',
    label: 'Codex',
    title: 'Codex CLI via Ollama app-server',
    connected: true,
  }),
  Object.freeze({
    id: 'opencode',
    label: 'OpenCode',
    title: 'OpenCode via WSL, dialogue non interactif',
    connected: true,
  }),
]);

export function normalizeCodingAgent(value) {
  const raw = String(value || '').trim().toLowerCase();
  if (raw === 'codex-cli' || raw === 'codex cli') return DEFAULT_CODING_AGENT;
  if (raw === 'claude-code' || raw === 'claude code') return 'claude';
  return CODING_AGENTS.some((agent) => agent.id === raw) ? raw : DEFAULT_CODING_AGENT;
}

export function getCodingAgentDefinition(value) {
  const id = normalizeCodingAgent(value);
  return CODING_AGENTS.find((agent) => agent.id === id) || CODING_AGENTS[0];
}
