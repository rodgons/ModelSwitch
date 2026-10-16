import { describe, expect, it } from 'vitest'
import { checkUploadFile, maxUploadBytes, readUploadErrors } from './upload'

describe('dataset upload file check', () => {
  it('accepts a JSONL file up to 10 MiB', () => {
    expect(maxUploadBytes).toBe(10 * 1024 * 1024)
    expect(checkUploadFile({ name: 'cases.jsonl', size: maxUploadBytes })).toBeNull()
    expect(checkUploadFile({ name: 'cases.ndjson', size: 12 })).toBeNull()
  })

  it('rejects a file over 10 MiB before sending it', () => {
    expect(checkUploadFile({ name: 'cases.jsonl', size: maxUploadBytes + 1 })).toMatch(/10 MiB/)
  })

  it('rejects an empty file', () => {
    expect(checkUploadFile({ name: 'cases.jsonl', size: 0 })).toMatch(/empty/)
  })

  it('asks for a JSONL file by extension', () => {
    expect(checkUploadFile({ name: 'cases.csv', size: 10 })).toMatch(/\.jsonl/)
    expect(checkUploadFile({ name: 'CASES.JSONL', size: 10 })).toBeNull()
  })
})

describe('dataset upload validation errors', () => {
  it('groups errors by 1-based line number, in line order', () => {
    const read = readUploadErrors({
      'lines[12].expected': ['Must be a non-empty object.'],
      'lines[3].id': ['Is repeated.'],
      'lines[3]': ['Is not a JSON object.'],
    })
    expect(read.lines).toEqual([
      {
        line: 3,
        problems: [
          { field: null, messages: ['Is not a JSON object.'] },
          { field: 'id', messages: ['Is repeated.'] },
        ],
      },
      { line: 12, problems: [{ field: 'expected', messages: ['Must be a non-empty object.'] }] },
    ])
    expect(read.general).toEqual([])
  })

  it('keeps nested field paths after the line', () => {
    expect(readUploadErrors({ 'lines[1].input.text': ['x'] }).lines[0]!.problems[0]!.field).toBe(
      'input.text',
    )
  })

  it('reports body errors and any unrecognized key as about the whole upload', () => {
    const read = readUploadErrors({
      body: ['Only the first 100 invalid lines are listed.'],
      other: ['Odd.'],
    })
    expect(read.general).toEqual(['Only the first 100 invalid lines are listed.', 'other: Odd.'])
    expect(read.lines).toEqual([])
  })
})
