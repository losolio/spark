import http from 'k6/http';
import exec from 'k6/execution';
import { check } from 'k6';
import { patient } from './patients.js';
import {
  ORGANIZATIONS, PRACTITIONERS, conditions, encounter, medicationRequest, observations, organization, practitioner,
} from './clinical.js';

const COUNT = parseInt(__ENV.COUNT || '100000');
// Each patient also gets an encounter, ten observations, one or two conditions and a prescription, unless
// CLINICAL is false.
const CLINICAL = (__ENV.CLINICAL || 'true') === 'true';

export const options = {
  scenarios: {
    load: { executor: 'shared-iterations', vus: parseInt(__ENV.VUS || '50'), iterations: COUNT, maxDuration: '180m' },
  },
};

const params = { headers: { 'Content-Type': 'application/fhir+json', 'Accept': 'application/fhir+json' } };

function post(type, body, name) {
  const res = http.post(`${__ENV.BASE_URL}/${type}`, JSON.stringify(body), { ...params, tags: { name: name || type } });
  check(res, { [`${type} created`]: (r) => r.status === 201 });
  return res.status === 201 ? `${type}/${res.json().id}` : null;
}

/** The practitioners and organizations every patient is spread over, created once. */
export function setup() {
  if (!CLINICAL) return { practitioners: [], organizations: [] };

  return {
    practitioners: Array.from({ length: PRACTITIONERS }, (_, n) => post('Practitioner', practitioner(n), 'setup')),
    organizations: Array.from({ length: ORGANIZATIONS }, (_, n) => post('Organization', organization(n), 'setup')),
  };
}

export default function (pool) {
  const i = exec.scenario.iterationInTest;
  const patientReference = post('Patient', patient(i));
  if (!CLINICAL || patientReference === null) return;

  const practitionerReference = pool.practitioners[i % pool.practitioners.length];
  const organizationReference = pool.organizations[i % pool.organizations.length];
  const encounterReference = post('Encounter', encounter(i, patientReference, practitionerReference, organizationReference));
  if (encounterReference === null) return;

  for (const body of observations(i, patientReference, encounterReference, practitionerReference)) {
    post('Observation', body);
  }
  for (const body of conditions(i, patientReference, encounterReference, practitionerReference)) {
    post('Condition', body);
  }
  post('MedicationRequest', medicationRequest(i, patientReference, encounterReference, practitionerReference));
}
