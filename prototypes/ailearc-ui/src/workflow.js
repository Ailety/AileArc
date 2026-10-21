export function archiveName(value, format) {
  return value.trim().replace(/\.(zip|7z)$/i, "") + "." + format;
}

export function nameError(value) {
  const name = value.trim().replace(/\.(zip|7z)$/i, "");
  if (!name) return "请输入压缩包名称。";
  if (/[<>:"/\\|?*\x00-\x1f]/.test(name) || /[. ]$/.test(name))
    return "名称不能包含路径符号或以句点结尾。";
  if (/^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\.|$)/i.test(name))
    return "请换一个名称，这个名称由 Windows 保留。";
  return "";
}

export function destinationError(value) {
  return /^(?:[a-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+(?:[\\/]|$))/i.test(
    value.trim(),
  )
    ? ""
    : "请输入完整目标路径，例如 D:\\Downloads\\project-kit。";
}

export function extractionTargets(items, destination, keepPaths) {
  const root = destination.trim().replace(/[\\/]+$/, "");
  return items.map(
    (item) =>
      root +
      "\\" +
      (keepPaths ? item.parent : "").replaceAll("/", "\\") +
      item.name,
  );
}
