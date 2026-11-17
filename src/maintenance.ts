export function shouldRotate(size: number, limit = 1_048_576): boolean {
  return Number.isFinite(size) && Number.isFinite(limit) && limit >= 0 && size > limit;
}
