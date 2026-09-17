import http from 'k6/http';
import { check } from 'k6';
import { patientValues } from './patients.js';

const COUNT = parseInt(__ENV.COUNT || '100000');
const QUERY = __ENV.QUERY;
const params = { headers: { 'Accept': 'application/fhir+json' }, tags: { query: QUERY } };

export const options = {
  scenarios: { search: { executor: 'constant-vus', vus: parseInt(__ENV.VUS || '10'), duration: __ENV.DURATION || '30s' } },
};

function pick() { return Math.floor(Math.random() * COUNT); }

function between(min, max) { return min + Math.floor(Math.random() * (max - min + 1)); }

const queries = {
  // --- Patient ---
  // Exactly one match.
  identifier: () => `Patient?identifier=urn:oid:2.16.578.1.12.4.1.4.1|${patientValues(pick()).identifier}`,
  // A handful of matches: about COUNT / 36500 per day.
  birthdate: () => `Patient?birthdate=${patientValues(pick()).birthDate}`,
  // About 2% of the patients share a family name, so this returns around two thousand matches.
  family: () => `Patient?family=${encodeURIComponent(patientValues(pick()).family.slice(0, 4))}&_count=10`,
  // A given name born in a given year: tens of matches.
  given_birthyear: () => { const v = patientValues(pick()); return `Patient?given=${encodeURIComponent(v.given)}&birthdate=${v.birthDate.slice(0, 4)}&_count=10`; },
  // Counting half of the patients.
  gender_count: () => `Patient?gender=female&_summary=count`,

  // --- Clinical, when the data was loaded with CLINICAL=true ---
  // One blood pressure panel per patient: a token search that matches everything of its type.
  observation_code: () => `Observation?code=http://loinc.org|85354-9&_count=10`,
  // A laboratory result above a value: a quantity comparison in the canonical unit.
  observation_value: () => `Observation?code=http://loinc.org|2339-0&value-quantity=gt${between(10, 14)}|http://unitsofmeasure.org|mmol/L&_count=10`,
  // Observations of one category in a period: a token and a date range together.
  observation_category_date: () => `Observation?category=laboratory&date=${between(2022, 2025)}-${String(between(1, 12)).padStart(2, '0')}&_count=10`,
  // The observations of one patient, found by the identifier of the patient: a chain that ends in a token.
  observation_patient: () => `Observation?subject:Patient.identifier=urn:oid:2.16.578.1.12.4.1.4.1|${patientValues(pick()).identifier}&_count=10`,
  // Observations of patients with a family name: a chain that ends in a string.
  observation_chain: () => `Observation?subject:Patient.family=${encodeURIComponent(patientValues(pick()).family.slice(0, 4))}&_count=10`,
  // Everything one practitioner measured: a reference search over a hundredth of the observations.
  observation_performer: () => `Observation?performer:Practitioner.identifier=urn:oid:2.16.578.1.12.4.1.4.4|${900000 + between(0, 99)}&_count=10`,

  // --- Condition, Encounter and MedicationRequest ---
  // One diagnosis code: a sixth of the patients.
  condition_code: () => `Condition?code=http://snomed.info/sct|38341003&_count=10`,
  // Diagnoses that started in a period.
  condition_onset: () => `Condition?onset-date=${between(2016, 2025)}-${String(between(1, 12)).padStart(2, '0')}&_count=10`,
  // Emergency encounters in a month: a token and a period.
  encounter_class_date: () => `Encounter?class=http://terminology.hl7.org/CodeSystem/v3-ActCode|EMER&date=${between(2022, 2025)}-${String(between(1, 12)).padStart(2, '0')}&_count=10`,
  // Encounters in a month.
  encounter_date: () => `Encounter?date=${between(2022, 2025)}-${String(between(1, 12)).padStart(2, '0')}&_count=10`,
  // One medication: a fifth of the patients.
  medication_code: () => `MedicationRequest?code=http://snomed.info/sct|386864001&_count=10`,
};

export default function () {
  const res = http.get(`${__ENV.BASE_URL}/${queries[QUERY]()}`, params);
  check(res, { 'ok': (r) => r.status === 200 });
}
