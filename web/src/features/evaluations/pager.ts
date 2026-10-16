/** Where a cursor-paginated list is (api.md section 3): the current cursor and the cursors of the pages before it. */
export interface PageState {
  cursor: string | null
  back: (string | null)[]
}

export const firstPage = (): PageState => ({ cursor: null, back: [] })

export const nextPage = (page: PageState, nextCursor: string): PageState => ({
  cursor: nextCursor,
  back: [...page.back, page.cursor],
})

export function previousPage(page: PageState): PageState {
  if (!page.back.length) return firstPage()
  const back = page.back.slice(0, -1)
  return { cursor: page.back[page.back.length - 1] ?? null, back }
}
