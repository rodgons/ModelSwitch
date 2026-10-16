import { useMutation, useQueryClient } from '@tanstack/vue-query'
import { computed, ref } from 'vue'
import { ApiError } from '@/api/client'

/**
 * A form submission: runs `submit`, refreshes the queries whose keys start with `invalidate`, and keeps the server's validation
 * errors per field (400 `validation_failed`, api.md section 3) so each Field can show its own.
 */
export function useFormMutation<TInput, TResult>(
  submit: (input: TInput) => Promise<TResult>,
  invalidate: readonly (readonly unknown[])[],
  onSuccess?: (result: TResult) => void,
) {
  const queries = useQueryClient()
  const failure = ref<ApiError | Error | null>(null)
  const mutation = useMutation({
    mutationFn: submit,
    onMutate: () => (failure.value = null),
    onSuccess: async (result) => {
      await Promise.all(invalidate.map((queryKey) => queries.invalidateQueries({ queryKey })))
      onSuccess?.(result)
    },
    onError: (error) => (failure.value = error),
  })
  const fieldErrors = computed(() =>
    failure.value instanceof ApiError ? failure.value.fieldErrors : {},
  )
  const errorsFor = (field: string) =>
    fieldErrors.value[field] ?? fieldErrors.value[field.charAt(0).toUpperCase() + field.slice(1)]
  /** A failure no field can show (not a validation error, or one about the whole body). */
  const generalError = computed(() =>
    failure.value && Object.keys(fieldErrors.value).length === 0 ? failure.value : null,
  )
  return { mutation, failure, generalError, errorsFor, reset: () => (failure.value = null) }
}
