import { describe, it, expect } from 'vitest';
import { partsInventoryKeys } from '../hooks/usePartsInventory';
import {
  getErrorMessage,
  getErrorStatus,
  getProblemCode,
  getWrongBinMismatches,
  isFeatureDisabledError,
  isPartMappingRequiredError,
  isWrongBinError,
} from '../utils/problemDetails';

describe('partsInventoryKeys', () => {
  it('scopes all keys under the parts-inventory root', () => {
    expect(partsInventoryKeys.all[0]).toBe('parts-inventory');
    expect(partsInventoryKeys.parts()[0]).toBe('parts-inventory');
    expect(partsInventoryKeys.bins()[0]).toBe('parts-inventory');
    expect(partsInventoryKeys.mappings()[0]).toBe('parts-inventory');
    expect(partsInventoryKeys.reorder()[0]).toBe('parts-inventory');
  });

  it('partsList key varies by includeInactive flag', () => {
    expect(partsInventoryKeys.partsList(true)).not.toEqual(
      partsInventoryKeys.partsList(false)
    );
  });

  it('part detail key encodes the sku', () => {
    expect(partsInventoryKeys.part('SKU-1')).toContain('SKU-1');
    expect(partsInventoryKeys.part('SKU-1')).not.toEqual(partsInventoryKeys.part('SKU-2'));
  });

  it('adjustments key differs by limit', () => {
    expect(partsInventoryKeys.adjustments('S', 25)).not.toEqual(
      partsInventoryKeys.adjustments('S', 100)
    );
  });

  it('mappingList treats undefined sku as "all"', () => {
    expect(partsInventoryKeys.mappingList(undefined)).toContain('all');
    expect(partsInventoryKeys.mappingList('S')).toContain('S');
  });
});

describe('problemDetails utils', () => {
  it('recognises wrongBin conflict from axios error.response.data', () => {
    const err = {
      response: { status: 409, data: { code: 'wrongBin', mismatches: [] } },
    };
    expect(isWrongBinError(err)).toBe(true);
    expect(getProblemCode(err)).toBe('wrongBin');
    expect(getErrorStatus(err)).toBe(409);
  });

  it('recognises wrongBin conflict from apiClient-wrapped data (post-interceptor shape)', () => {
    // The response interceptor flattens axios errors into
    // { message, statusCode, details, data } — the raw problem body lives on
    // `data`, while `details` stays a string for legacy callers.
    const err = {
      statusCode: 409,
      message: 'Wrong bin',
      details: undefined,
      data: { code: 'wrongBin', mismatches: [{ partSku: 'S', scannedBinCode: 'X' }] },
    };
    expect(isWrongBinError(err)).toBe(true);
    expect(getErrorStatus(err)).toBe(409);
    expect(getWrongBinMismatches(err)).toEqual([{ partSku: 'S', scannedBinCode: 'X' }]);
  });

  it('recognises partMappingRequired and featureDisabled codes', () => {
    const mapping = { response: { data: { code: 'partMappingRequired' } } };
    const disabled = { response: { data: { code: 'featureDisabled' } } };
    expect(isPartMappingRequiredError(mapping)).toBe(true);
    expect(isFeatureDisabledError(disabled)).toBe(true);
  });

  it('filters malformed mismatches out', () => {
    const err = {
      response: {
        data: {
          code: 'wrongBin',
          mismatches: [{ partSku: 'S', scannedBinCode: 'X' }, { partSku: 5 }, null],
        },
      },
    };
    expect(getWrongBinMismatches(err)).toHaveLength(1);
  });

  it('returns null when payload is not an object', () => {
    expect(getProblemCode('boom')).toBeNull();
    expect(getProblemCode(null)).toBeNull();
    expect(getProblemCode(undefined)).toBeNull();
  });

  it('getErrorMessage prefers ProblemDetails.detail, falls back to message, then fallback string', () => {
    expect(getErrorMessage({ response: { data: { detail: 'bad delta' } } })).toBe('bad delta');
    expect(getErrorMessage({ message: 'axios err' })).toBe('axios err');
    expect(getErrorMessage(null, 'default')).toBe('default');
  });

  it('getErrorMessage reads ProblemDetails from the post-interceptor data field', () => {
    // Real prod shape: interceptor moves the problem body to `data`.
    expect(
      getErrorMessage({ statusCode: 409, message: 'Request failed with status code 409', data: { detail: 'On-hand cannot go below zero' } })
    ).toBe('On-hand cannot go below zero');
    expect(
      getErrorMessage({ statusCode: 409, message: 'boom', data: { message: 'Insufficient stock' } })
    ).toBe('Insufficient stock');
  });

  it('getErrorMessage never returns the axios boilerplate; uses fallback instead', () => {
    expect(
      getErrorMessage({ statusCode: 409, message: 'Request failed with status code 409' }, 'Failed to adjust stock')
    ).toBe('Failed to adjust stock');
    expect(getErrorMessage('Request failed with status code 500', 'nope')).toBe('nope');
  });
});
