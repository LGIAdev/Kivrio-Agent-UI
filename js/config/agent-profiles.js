export const DEFAULT_AGENT_PROFILE = 'deep';

export const AGENT_PROFILES = Object.freeze([
  Object.freeze({
    id: 'fast',
    label: 'Rapide',
    title: 'Reponses courtes, contexte limite, latence reduite',
  }),
  Object.freeze({
    id: 'deep',
    label: 'Profond',
    title: 'Analyse plus complete, contexte plus large, latence plus longue',
  }),
]);

export function normalizeAgentProfile(value) {
  const raw = String(value || '').trim().toLowerCase();
  return AGENT_PROFILES.some((profile) => profile.id === raw) ? raw : DEFAULT_AGENT_PROFILE;
}

export function getAgentProfileDefinition(value) {
  const id = normalizeAgentProfile(value);
  return AGENT_PROFILES.find((profile) => profile.id === id) || AGENT_PROFILES[0];
}
