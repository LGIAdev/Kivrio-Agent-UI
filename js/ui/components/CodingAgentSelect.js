import { CODING_AGENTS, getCodingAgentDefinition, normalizeCodingAgent } from '../../config/coding-agents.js';
import { getCodingAgent, setCodingAgent } from '../../store/settings.js';
import { Store } from '../../store/conversations.js';

function mountOptions(el) {
  el.replaceChildren();
  for (const agent of CODING_AGENTS) {
    const option = document.createElement('option');
    option.value = agent.id;
    option.textContent = agent.label;
    option.title = agent.title;
    el.appendChild(option);
  }
}

function currentConversation() {
  try {
    const id = Store.currentId?.();
    return id ? Store.get(id) : null;
  } catch (_) {
    return null;
  }
}

function hasMessages(conversation) {
  if (!conversation) return false;
  return Number(conversation.messageCount || 0) > 0
    || (Array.isArray(conversation.messages) && conversation.messages.length > 0);
}

function syncAgentSelect(el) {
  const conversation = currentConversation();
  const locked = hasMessages(conversation);
  const agentId = locked
    ? normalizeCodingAgent(conversation?.agent)
    : getCodingAgent();
  const agent = getCodingAgentDefinition(agentId);
  const picker = el.closest('.model-picker');

  el.value = agent.id;
  el.disabled = locked;
  el.title = locked
    ? `Agent verrouille pour cette conversation: ${agent.label}`
    : `${agent.label}\n${agent.title}`;
  picker?.classList.toggle('is-locked', locked);
}

export function mountCodingAgentSelect() {
  const el = document.querySelector('#coding-agent-select');
  if (!el) return;

  mountOptions(el);
  syncAgentSelect(el);

  el.addEventListener('change', (event) => {
    const agent = event.target?.value;
    setCodingAgent(agent);
    syncAgentSelect(el);
  });

  document.addEventListener('settings:coding-agent-changed', () => syncAgentSelect(el));
  document.addEventListener('conversation:current-changed', () => syncAgentSelect(el));
}
