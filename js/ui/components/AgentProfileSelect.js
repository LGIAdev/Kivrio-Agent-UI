import { AGENT_PROFILES, getAgentProfileDefinition } from '../../config/agent-profiles.js';
import { getAgentProfile, setAgentProfile } from '../../store/settings.js';

function mountOptions(el) {
  el.replaceChildren();
  for (const profile of AGENT_PROFILES) {
    const option = document.createElement('option');
    option.value = profile.id;
    option.textContent = profile.label;
    option.title = profile.title;
    el.appendChild(option);
  }
}

function syncTitle(el, profileId) {
  const profile = getAgentProfileDefinition(profileId);
  el.title = `Profil agent: ${profile.label}\n${profile.title}`;
}

export function mountAgentProfileSelect() {
  const el = document.querySelector('#agent-profile-select');
  if (!el) return;

  mountOptions(el);
  el.value = getAgentProfile();
  syncTitle(el, el.value);

  el.addEventListener('change', (event) => {
    const profile = event.target?.value;
    setAgentProfile(profile);
    syncTitle(el, getAgentProfile());
  });

  document.addEventListener('settings:agent-profile-changed', (event) => {
    const profile = String(event.detail || getAgentProfile());
    if (el.value !== profile) el.value = profile;
    syncTitle(el, profile);
  });
}
