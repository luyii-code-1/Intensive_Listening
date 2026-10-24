"""Validate desktop update identities accepted by the management service."""
from datetime import datetime
import re


def valid_version(value: str) -> bool:
    match = re.fullmatch(r"v?(\d+\.\d+\.\d+(?:\.\d+)?)(?:(?:-(insider|beta)\.(\d{8})(?:\.(\d+))?)|(?: (Insider Preview|Beta) (\d{4})(?: (\d+))?))?", value)
    if not match or any(int(part) > 2147483647 for part in match[1].split('.')):
        return False
    channel = match[2] or match[5]
    if not channel:
        return True
    date, sequence = match[3] or match[6], match[4] or match[7]
    try:
        datetime.strptime(('2000' + date) if len(date) == 4 else date, '%Y%m%d')
        return bool(sequence and 0 < int(sequence) <= 2147483647) if channel in ('insider', 'Insider Preview') else not sequence
    except ValueError:
        return False
