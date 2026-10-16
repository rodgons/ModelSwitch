/** A Dataset's task kind (api.md section 3): it picks the Evaluators that fit and how `expected` is validated. */
export const taskKinds = [
  { value: 'extraction', label: 'Extraction' },
  { value: 'classification', label: 'Classification' },
  { value: 'free_text', label: 'Free text' },
]

export const taskKindLabel = (kind: string) =>
  taskKinds.find((k) => k.value === kind)?.label ?? kind
