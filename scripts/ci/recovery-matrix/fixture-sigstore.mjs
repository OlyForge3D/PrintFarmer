import crypto from 'node:crypto';
import fs from 'node:fs';

import { releaseSigningIdentity } from '../offline-update-bundle.mjs';

const fixtureOrganization = 'printfarmer-recovery-matrix-fixture';
const fulcioIssuerOidV1 = '1.3.6.1.4.1.57264.1.1';
const fulcioIssuerOidV2 = '1.3.6.1.4.1.57264.1.8';
const sctListOid = '1.3.6.1.4.1.11129.2.4.2';
const oidSan = '2.5.29.17';
const oidKeyUsage = '2.5.29.15';
const oidExtKeyUsage = '2.5.29.37';
const oidBasicConstraints = '2.5.29.19';
const oidSubjectKeyIdentifier = '2.5.29.14';
const oidAuthorityKeyIdentifier = '2.5.29.35';
const oidCodeSigning = '1.3.6.1.5.5.7.3.3';
const oidEcPublicKey = '1.2.840.10045.2.1';
const oidPrime256v1 = '1.2.840.10045.3.1.7';
const oidEcdsaWithSha256 = '1.2.840.10045.4.3.2';
const oidOrganization = '2.5.4.10';
const oidCommonName = '2.5.4.3';

const asn1 = {
  sequence: (...parts) => tlv(0x30, Buffer.concat(parts)),
  set: (...parts) => tlv(0x31, Buffer.concat(parts)),
  explicit: (tag, value) => tlv(0xa0 + tag, value),
  integer: (value) => {
    let bytes;
    if (typeof value === 'bigint') {
      let hex = value.toString(16);
      if (hex.length % 2) hex = `0${hex}`;
      bytes = Buffer.from(hex || '00', 'hex');
    } else if (Buffer.isBuffer(value)) {
      bytes = Buffer.from(value);
    } else {
      bytes = Buffer.from([value]);
    }
    while (bytes.length > 1 && bytes[0] === 0 && (bytes[1] & 0x80) === 0) bytes = bytes.subarray(1);
    if (bytes[0] & 0x80) bytes = Buffer.concat([Buffer.from([0]), bytes]);
    return tlv(0x02, bytes);
  },
  oid: (oid) => tlv(0x06, encodeOid(oid)),
  utf8: (value) => tlv(0x0c, Buffer.from(value, 'utf8')),
  ia5: (value) => tlv(0x16, Buffer.from(value, 'ascii')),
  bool: (value) => tlv(0x01, Buffer.from([value ? 0xff : 0x00])),
  octet: (value) => tlv(0x04, Buffer.from(value)),
  bitString: (value, unusedBits = 0) => tlv(0x03, Buffer.concat([Buffer.from([unusedBits]), Buffer.from(value)])),
  utcTime: (value) => tlv(0x17, Buffer.from(toAsn1UtcTime(value), 'ascii')),
};

export function fixtureReleaseIdentity(channel) {
  return releaseSigningIdentity(channel);
}

export function createFixtureSigstoreRoot({ now = new Date() } = {}) {
  const validAt = new Date(now);
  const validFrom = new Date(validAt.getTime() - 60 * 60 * 1000);
  const validUntil = new Date(validAt.getTime() + 24 * 60 * 60 * 1000);
  let state = {
    ca: generateKeyPair(),
    rekor: generateKeyPair(),
    ct: generateKeyPair(),
  };

  const caSpki = publicSpki(state.ca);
  const caCertificate = createCertificate({
    serial: randomSerial(),
    subject: name([
      [oidOrganization, fixtureOrganization],
      [oidCommonName, 'fixture-fulcio-root'],
    ]),
    issuer: name([
      [oidOrganization, fixtureOrganization],
      [oidCommonName, 'fixture-fulcio-root'],
    ]),
    publicKey: state.ca,
    issuerKey: state.ca,
    issuerSpki: caSpki,
    notBefore: validFrom,
    notAfter: validUntil,
    extensions: caExtensions(caSpki),
  }).certificate;

  const trustedRoot = {
    mediaType: 'application/vnd.dev.sigstore.trustedroot+json;version=0.1',
    tlogs: [transparencyLog('https://fixture.invalid/rekor', state.rekor, validFrom, validUntil)],
    certificateAuthorities: [{
      subject: {
        organization: fixtureOrganization,
        commonName: 'fixture-fulcio-root',
      },
      uri: 'https://fixture.invalid/fulcio',
      certChain: { certificates: [{ rawBytes: b64(caCertificate) }] },
      validFor: timeRange(validFrom, validUntil),
      operator: 'fixture.invalid',
    }],
    ctlogs: [transparencyLog('https://fixture.invalid/ct', state.ct, validFrom, validUntil)],
    timestampAuthorities: [],
  };

  const fingerprint = hex(sha256(canonicalize(trustedRoot)));

  return {
    trustedRoot,
    fingerprint,
    writeTrustedRoot(path) {
      fs.writeFileSync(path, `${JSON.stringify(trustedRoot, undefined, 2)}\n`);
    },
    signBlob(bytes, { identity, issuer = 'https://token.actions.githubusercontent.com' } = {}) {
      if (state === undefined) throw new Error('fixture Sigstore root has been disposed');
      if (typeof identity !== 'string' || identity.length === 0) {
        throw new Error('identity is required');
      }
      if (typeof issuer !== 'string' || issuer.length === 0) {
        throw new Error('issuer is required');
      }
      return signBlobWithRoot({
        bytes: Buffer.from(bytes),
        identity,
        issuer,
        validAt,
        caKey: state.ca,
        caSpki,
        caCertificate,
        rekorKey: state.rekor,
        ctKey: state.ct,
      });
    },
    dispose() {
      state = undefined;
    },
  };
}

function signBlobWithRoot({ bytes, identity, issuer, validAt, caKey, caSpki, caCertificate, rekorKey, ctKey }) {
  const signingKey = generateKeyPair();
  const signingSpki = publicSpki(signingKey);
  const notBefore = new Date(validAt.getTime() - 5 * 60 * 1000);
  const notAfter = new Date(validAt.getTime() + 10 * 60 * 1000);
  const integratedTime = Math.floor(validAt.getTime() / 1000);
  const leafSerial = randomSerial();
  const tbsWithoutSct = leafTbs({
    serial: leafSerial,
    subjectPublicKey: signingKey,
    issuerName: fixtureCaName(),
    subjectName: name([[oidCommonName, 'fixture-release-signer']]),
    notBefore,
    notAfter,
    identity,
    issuer,
    authoritySpki: caSpki,
    sctList: undefined,
  });
  const sctList = createSctList({
    tbsCertificate: tbsWithoutSct,
    issuerSpki: caSpki,
    ctKey,
    timestamp: validAt.getTime(),
  });
  const { certificate: leafCertificate } = createCertificate({
    tbsCertificate: leafTbs({
      serial: leafSerial,
      subjectPublicKey: signingKey,
      issuerName: fixtureCaName(),
      subjectName: name([[oidCommonName, 'fixture-release-signer']]),
      notBefore,
      notAfter,
      identity,
      issuer,
      authoritySpki: caSpki,
      sctList,
    }),
    issuerKey: caKey,
  });

  const digest = sha256(bytes);
  const signature = crypto.sign('sha256', bytes, signingKey);
  const canonicalizedBody = rekorBody({ bytes, signature, certificate: leafCertificate });
  const logId = sha256(publicSpki(rekorKey));
  const rootHash = sha256(Buffer.concat([Buffer.from([0]), canonicalizedBody]));
  const checkpoint = checkpointEnvelope({
    origin: new URL('https://fixture.invalid/rekor').hostname,
    rootHash,
    treeSize: 1,
    rekorKey,
  });
  const signedEntryTimestamp = crypto.sign('sha256', canonicalize({
    body: b64(canonicalizedBody),
    integratedTime,
    logID: hex(logId),
    logIndex: 0,
  }), rekorKey);

  return {
    mediaType: 'application/vnd.dev.sigstore.bundle.v0.3+json',
    verificationMaterial: {
      certificate: { rawBytes: b64(leafCertificate) },
      tlogEntries: [{
        logIndex: '0',
        logId: { keyId: b64(logId) },
        kindVersion: { kind: 'hashedrekord', version: '0.0.1' },
        integratedTime: String(integratedTime),
        inclusionPromise: { signedEntryTimestamp: b64(signedEntryTimestamp) },
        inclusionProof: {
          logIndex: '0',
          rootHash: b64(rootHash),
          treeSize: '1',
          hashes: [],
          checkpoint: { envelope: checkpoint },
        },
        canonicalizedBody: b64(canonicalizedBody),
      }],
    },
    messageSignature: {
      messageDigest: {
        algorithm: 'SHA2_256',
        digest: b64(digest),
      },
      signature: b64(signature),
    },
  };
}

function generateKeyPair() {
  return crypto.generateKeyPairSync('ec', { namedCurve: 'prime256v1' }).privateKey;
}

function publicSpki(privateKey) {
  return crypto.createPublicKey(privateKey).export({ type: 'spki', format: 'der' });
}

function transparencyLog(baseUrl, key, start, end) {
  const spki = publicSpki(key);
  const logId = sha256(spki);
  return {
    baseUrl,
    hashAlgorithm: 'SHA2_256',
    publicKey: {
      rawBytes: b64(spki),
      keyDetails: 'PKIX_ECDSA_P256_SHA_256',
      validFor: timeRange(start, end),
    },
    logId: { keyId: b64(logId) },
    checkpointKeyId: { keyId: b64(logId.subarray(0, 4)) },
    operator: 'fixture.invalid',
  };
}

function timeRange(start, end) {
  return { start: start.toISOString(), end: end.toISOString() };
}

function fixtureCaName() {
  return name([
    [oidOrganization, fixtureOrganization],
    [oidCommonName, 'fixture-fulcio-root'],
  ]);
}

function caExtensions(spki) {
  return [
    extension(oidBasicConstraints, asn1.sequence(asn1.bool(true)), true),
    extension(oidKeyUsage, asn1.bitString(Buffer.from([0x06]), 1), true),
    extension(oidSubjectKeyIdentifier, asn1.octet(keyIdentifier(spki))),
  ];
}

function leafTbs({
  serial,
  subjectPublicKey,
  issuerName,
  subjectName,
  notBefore,
  notAfter,
  identity,
  issuer,
  authoritySpki,
  sctList,
}) {
  const signingSpki = publicSpki(subjectPublicKey);
  const extensions = [
    extension(oidKeyUsage, asn1.bitString(Buffer.from([0x80]), 7), true),
    extension(oidExtKeyUsage, asn1.sequence(asn1.oid(oidCodeSigning))),
    extension(oidSan, asn1.sequence(tlv(0x86, Buffer.from(identity, 'ascii')))),
    extension(fulcioIssuerOidV1, Buffer.from(issuer, 'utf8')),
    extension(fulcioIssuerOidV2, asn1.utf8(issuer)),
    extension(oidBasicConstraints, asn1.sequence()),
    extension(oidSubjectKeyIdentifier, asn1.octet(keyIdentifier(signingSpki))),
    extension(oidAuthorityKeyIdentifier, asn1.sequence(tlv(0x80, keyIdentifier(authoritySpki)))),
  ];
  if (sctList !== undefined) extensions.push(extension(sctListOid, asn1.octet(sctList)));

  return tbsCertificate({
    serial,
    issuer: issuerName,
    subject: subjectName,
    notBefore,
    notAfter,
    subjectPublicKeyInfo: signingSpki,
    extensions,
  });
}

function createCertificate({ serial, subject, issuer, publicKey, issuerKey, issuerSpki, notBefore, notAfter, extensions, tbsCertificate: suppliedTbs }) {
  const tbs = suppliedTbs ?? tbsCertificate({
    serial,
    issuer,
    subject,
    notBefore,
    notAfter,
    subjectPublicKeyInfo: publicSpki(publicKey),
    extensions,
    issuerSpki,
  });
  const signature = crypto.sign('sha256', tbs, issuerKey);
  return {
    tbsCertificate: tbs,
    certificate: asn1.sequence(
      tbs,
      algorithmIdentifier(oidEcdsaWithSha256),
      asn1.bitString(signature),
    ),
  };
}

function tbsCertificate({ serial, issuer, subject, notBefore, notAfter, subjectPublicKeyInfo, extensions }) {
  return asn1.sequence(
    asn1.explicit(0, asn1.integer(2)),
    asn1.integer(serial),
    algorithmIdentifier(oidEcdsaWithSha256),
    issuer,
    asn1.sequence(asn1.utcTime(notBefore), asn1.utcTime(notAfter)),
    subject,
    subjectPublicKeyInfo,
    asn1.explicit(3, asn1.sequence(...extensions)),
  );
}

function extension(oid, value, critical = false) {
  const parts = [asn1.oid(oid)];
  if (critical) parts.push(asn1.bool(true));
  parts.push(asn1.octet(value));
  return asn1.sequence(...parts);
}

function algorithmIdentifier(oid) {
  return asn1.sequence(asn1.oid(oid));
}

function name(attributes) {
  return asn1.sequence(
    ...attributes.map(([oid, value]) =>
      asn1.set(asn1.sequence(asn1.oid(oid), asn1.utf8(value)))),
  );
}

function keyIdentifier(spki) {
  return crypto.createHash('sha1').update(spki).digest();
}

function createSctList({ tbsCertificate, issuerSpki, ctKey, timestamp }) {
  const logId = sha256(publicSpki(ctKey));
  const signatureInput = Buffer.concat([
    Buffer.from([0, 0]),
    uint64(timestamp),
    Buffer.from([0, 1]),
    sha256(issuerSpki),
    uint24(tbsCertificate.length),
    tbsCertificate,
    uint16(0),
  ]);
  const signature = crypto.sign('sha256', signatureInput, ctKey);
  const sct = Buffer.concat([
    Buffer.from([0]),
    logId,
    uint64(timestamp),
    uint16(0),
    Buffer.from([4, 3]),
    uint16(signature.length),
    signature,
  ]);
  const item = Buffer.concat([uint16(sct.length), sct]);
  return Buffer.concat([uint16(item.length), item]);
}

function rekorBody({ bytes, signature, certificate }) {
  return canonicalize({
    apiVersion: '0.0.1',
    kind: 'hashedrekord',
    spec: {
      data: {
        hash: {
          algorithm: 'sha256',
          value: hex(sha256(bytes)),
        },
      },
      signature: {
        content: b64(signature),
        publicKey: {
          content: b64(Buffer.from(certificatePem(certificate), 'utf8')),
        },
      },
    },
  });
}

function checkpointEnvelope({ origin, rootHash, treeSize, rekorKey }) {
  const checkpoint = `${origin}\n${treeSize}\n${b64(rootHash)}\n`;
  const signature = crypto.sign('sha256', Buffer.from(checkpoint, 'utf8'), rekorKey);
  const keyHint = sha256(publicSpki(rekorKey)).subarray(0, 4);
  return `${checkpoint}\n\u2014 ${origin} ${b64(Buffer.concat([keyHint, signature]))}\n`;
}

function certificatePem(der) {
  const dash = '-'.repeat(5);
  const body = b64(der).replace(/.{1,64}/g, '$&\n').trimEnd();
  return `${dash}BEGIN CERTIFICATE${dash}\n${body}\n${dash}END CERTIFICATE${dash}\n`;
}

function canonicalize(value) {
  return Buffer.from(canonicalJson(value), 'utf8');
}

function canonicalJson(value) {
  if (value === undefined) return undefined;
  if (value === null || typeof value !== 'object') return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map((item) => canonicalJson(item)).join(',')}]`;
  return `{${Object.keys(value).sort().map((key) =>
    `${JSON.stringify(key)}:${canonicalJson(value[key])}`).join(',')}}`;
}

function tlv(tag, value) {
  return Buffer.concat([Buffer.from([tag]), lengthBytes(value.length), Buffer.from(value)]);
}

function lengthBytes(length) {
  if (length < 0x80) return Buffer.from([length]);
  const bytes = [];
  let remaining = length;
  while (remaining > 0) {
    bytes.unshift(remaining & 0xff);
    remaining >>= 8;
  }
  return Buffer.from([0x80 | bytes.length, ...bytes]);
}

function encodeOid(oid) {
  const parts = oid.split('.').map((part) => Number.parseInt(part, 10));
  const bytes = [parts[0] * 40 + parts[1]];
  for (const part of parts.slice(2)) {
    const encoded = [part & 0x7f];
    let value = part >> 7;
    while (value > 0) {
      encoded.unshift((value & 0x7f) | 0x80);
      value >>= 7;
    }
    bytes.push(...encoded);
  }
  return Buffer.from(bytes);
}

function toAsn1UtcTime(value) {
  return value.toISOString().slice(2).replace(/[-:T]/g, '').replace(/\.\d{3}Z$/, 'Z');
}

function randomSerial() {
  return crypto.randomBytes(16);
}

function sha256(bytes) {
  return crypto.createHash('sha256').update(bytes).digest();
}

function b64(bytes) {
  return Buffer.from(bytes).toString('base64');
}

function hex(bytes) {
  return Buffer.from(bytes).toString('hex');
}

function uint16(value) {
  const bytes = Buffer.alloc(2);
  bytes.writeUInt16BE(value);
  return bytes;
}

function uint24(value) {
  return Buffer.from([(value >> 16) & 0xff, (value >> 8) & 0xff, value & 0xff]);
}

function uint64(value) {
  const bytes = Buffer.alloc(8);
  bytes.writeBigUInt64BE(BigInt(value));
  return bytes;
}
