import { describe, expect, it } from 'vitest'
import { firstPage, nextPage, previousPage } from './pager'

describe('cursor pager', () => {
  it('walks forward and back through cursors', () => {
    let page = firstPage()
    expect(page).toEqual({ cursor: null, back: [] })
    page = nextPage(page, 'c1')
    page = nextPage(page, 'c2')
    expect(page).toEqual({ cursor: 'c2', back: [null, 'c1'] })
    page = previousPage(page)
    expect(page).toEqual({ cursor: 'c1', back: [null] })
    page = previousPage(page)
    expect(page).toEqual(firstPage())
  })

  it('stays on the first page when going back from it', () => {
    expect(previousPage(firstPage())).toEqual(firstPage())
  })
})
