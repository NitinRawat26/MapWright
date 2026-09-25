import { NewProfile } from '../core/models';

export interface ProfilePreset {
  id: string;
  label: string;
  summary: string;
  /** What to upload, one line per kind of file. */
  upload: string[];
  /** File types the file picker offers. */
  accept: string;
  rootHint: string;
  defaults: Pick<NewProfile, 'description' | 'noValues'>;
}

export const allExtensions = '.json,.xml,.xsd,.wsdl,.csv,.xlsx,.yaml,.yml,.pdf,.docx';

export const profilePresets: readonly ProfilePreset[] = [
  {
    id: 'json-rest',
    label: 'JSON REST API',
    summary: 'A system that sends or receives JSON over HTTP.',
    upload: [
      'Sample request bodies (.json): a few real or test payloads, e.g. one per product or entity type.',
      'The OpenAPI (JSON or YAML) or JSON Schema file, if there is one. It decides types and required fields.',
    ],
    accept: '.json,.yaml,.yml',
    rootHint: 'operationId, "POST /path" or schema name',
    defaults: { description: 'JSON REST API', noValues: false },
  },
  {
    id: 'soap-xml',
    label: 'SOAP/XML service',
    summary: 'A SOAP or XML web service described by a WSDL.',
    upload: [
      'Sample request messages (.xml), with or without the SOAP envelope.',
      'The WSDL and any XSD it uses. Leave Root empty with both, or give the WSDL operation with the WSDL only.',
    ],
    accept: '.xml,.xsd,.wsdl',
    rootHint: 'WSDL operation, e.g. SubmitApplication',
    defaults: { description: 'SOAP/XML service', noValues: false },
  },
  {
    id: 'xml-file',
    label: 'XML file or batch',
    summary: 'XML files exchanged by SFTP, a queue or a batch job.',
    upload: ['Sample files (.xml).', 'The XSD, if there is one. Give its root element when it declares several.'],
    accept: '.xml,.xsd',
    rootHint: 'XSD root element',
    defaults: { description: 'XML file interface', noValues: false },
  },
  {
    id: 'field-spec',
    label: 'Field spec',
    summary: 'A table with one row per field and its path, type and required flag.',
    upload: [
      'The field spec (.csv or .xlsx). Only a path column is needed, e.g. "Field Path", "XPath" or "JSON Path".',
      'Sample payloads (.json or .xml), if you have them, add observed values.',
    ],
    accept: '.csv,.xlsx,.json,.xml',
    rootHint: '',
    defaults: { description: 'From the field spec', noValues: false },
  },
  {
    id: 'data-dictionary',
    label: 'Data dictionary',
    summary: 'A data dictionary exported from a database, CRM or modelling tool.',
    upload: [
      'The dictionary (.csv or .xlsx). Columns such as "Field Name", "Entity" or "Parent", "Data Type", "Nullable" and "Allowed Values" are recognised.',
      'In Excel, every sheet with a field column is read; each sheet name is the parent of its fields.',
      'Sample payloads (.json or .xml), if you have them.',
    ],
    accept: '.csv,.xlsx,.json,.xml',
    rootHint: '',
    defaults: { description: 'From the data dictionary', noValues: false },
  },
  {
    id: 'document',
    label: 'PDF/Word spec',
    summary: 'An interface specification document with field tables.',
    upload: [
      'The specification (.pdf or .docx). Tables with a field column are read by rules.',
      'Text outside the tables is read only if you tick "Let AI read document text"; those fields are marked for review.',
      'Sample payloads (.json or .xml), if you have them, confirm what the document says.',
    ],
    accept: '.pdf,.docx,.json,.xml',
    rootHint: '',
    defaults: { description: 'From the interface specification', noValues: false },
  },
  {
    id: 'samples',
    label: 'Samples only',
    summary: 'Only example payloads, no documentation.',
    upload: [
      'Several sample payloads (.json or .xml) of the same message. The more variety, the better the required and repeating fields are guessed.',
    ],
    accept: '.json,.xml',
    rootHint: '',
    defaults: { description: 'Learned from samples', noValues: false },
  },
  {
    id: 'custom',
    label: 'Mixed/custom',
    summary: 'Any combination of the inputs above.',
    upload: [
      'Samples (JSON or XML), JSON Schema, OpenAPI (JSON or YAML), XSD, WSDL, field specs or data dictionaries (.csv, .xlsx), and PDF/Word specifications.',
    ],
    accept: allExtensions,
    rootHint: 'operation, schema or element',
    defaults: { description: undefined, noValues: false },
  },
];

export function presetById(id: string): ProfilePreset {
  return profilePresets.find((p) => p.id === id) ?? profilePresets[profilePresets.length - 1];
}

/** True when the file name has one of the extensions in an accept list such as ".json,.xml". */
export function accepts(accept: string, name: string): boolean {
  const lower = name.toLowerCase();
  return accept.split(',').some((extension) => lower.endsWith(extension));
}
