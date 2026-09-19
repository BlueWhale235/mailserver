import { api, jsonBody } from './api'

export type CertificateSource = 'ManagedUpload' | 'ExternalPath'

/** Matches the JSON shape of MailServer.Domain.DomainCertificate. */
export interface DomainCertificateModel {
  domainId?: string
  source: CertificateSource | string
  certificatePath?: string
  privateKeyPath?: string
  subject: string
  issuer: string
  subjectAlternativeNames: string
  thumbprint: string
  notBefore: string
  notAfter: string
  updatedAt: string
}

/** Matches DomainCertificateInspection returned by the inspect endpoints. */
export interface DomainCertificateInspection {
  subject: string
  issuer: string
  subjectAlternativeNames: string[]
  thumbprint: string
  notBefore: string
  notAfter: string
  hasMatchingPrivateKey: boolean
  supportsServerAuthentication: boolean
  coversMailHostName: boolean
}

export const maximumCertificateBytes = 1024 * 1024
export const maximumPrivateKeyBytes = 256 * 1024

export function isCertificateUsable(inspection: DomainCertificateInspection | null): boolean {
  return Boolean(inspection
    && inspection.hasMatchingPrivateKey
    && inspection.supportsServerAuthentication
    && inspection.coversMailHostName)
}

export function certificateUploadBody(certificate: File, privateKey: File, password: string): FormData {
  const form = new FormData()
  form.set('certificate', certificate, certificate.name)
  form.set('privateKey', privateKey, privateKey.name)
  if (password) form.set('password', password)
  return form
}

/** Wraps pasted PEM text so it can reuse the same multipart endpoints as file upload. */
export function pemTextFile(text: string, fileName: string): File {
  return new File([text], fileName, { type: 'application/x-pem-file' })
}

export function inspectCertificateUpload(domainId: string, form: FormData) {
  return api<DomainCertificateInspection>(`/domains/${encodeURIComponent(domainId)}/certificate/inspect-upload`, { method: 'POST', body: form })
}

export function uploadCertificate(domainId: string, form: FormData) {
  return api<DomainCertificateModel>(`/domains/${encodeURIComponent(domainId)}/certificate/upload`, { method: 'POST', body: form })
}

export function inspectCertificatePath(domainId: string, certificatePath: string, privateKeyPath: string) {
  return api<DomainCertificateInspection>(`/domains/${encodeURIComponent(domainId)}/certificate/inspect-path`, {
    method: 'POST',
    body: jsonBody({ certificatePath, privateKeyPath }),
  })
}

export function bindCertificatePath(domainId: string, certificatePath: string, privateKeyPath: string) {
  return api<DomainCertificateModel>(`/domains/${encodeURIComponent(domainId)}/certificate/path`, {
    method: 'PUT',
    body: jsonBody({ certificatePath, privateKeyPath }),
  })
}

export function deleteCertificate(domainId: string) {
  return api<void>(`/domains/${encodeURIComponent(domainId)}/certificate`, { method: 'DELETE' })
}
