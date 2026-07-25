using Avalonia.Controls;
using GhostShell.Application;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GhostShell.Browser;

/// <summary>
/// Owns all Avalonia WebView API usage so the package never appears in a
/// GhostSHELL application or host contract.
/// </summary>
internal sealed class AvaloniaNativeBrowserView : INativeBrowserView
{
    private const int MaximumNativeSnapshotBytes = 256 * 1024;
    private const int MaximumNativeClickResultBytes = 1_024;
    private const int MaximumNativeFillResultBytes = 1_024;
    private const int MaximumNativeCheckResultBytes = 1_024;
    private const int MaximumSnapshotNodes = 128;
    private const int MaximumSnapshotDepth = 32;
    private const int MaximumRoleBytes = 64;
    private const int MaximumNameBytes = 256;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private const string SnapshotScriptTemplate = """
        (() => {
          const registrySlot = __GHOSTSHELL_REGISTRY_SLOT__;
          const registrySecret = __GHOSTSHELL_REGISTRY_SECRET__;
          const snapshotNonce = __GHOSTSHELL_SNAPSHOT_NONCE__;
          const maximumNodes = 128;
          const maximumDepth = 32;
          const maximumVisitedElements = 4096;
          const maximumTraversalDepth = 64;
          const maximumTextSourceUnits = 1024;
          const maximumTextNodesPerLabel = 64;
          const maximumDirectTextNodes = 32;
          const maximumLabelledByIds = 16;
          const maximumLabels = 8;
          let remainingTextNodes = 4096;
          const whitespace = /\s/u;
          const excludedTags = new Set([
            "script", "style", "noscript", "template"
          ]);
          const actionableRoles = new Set([
            "button", "link", "checkbox", "radio", "textbox", "combobox",
            "menuitem", "option", "switch", "tab"
          ]);
          const utf8Width = codePoint => {
            if (codePoint <= 0x7f) return 1;
            if (codePoint <= 0x7ff) return 2;
            if (codePoint <= 0xffff) return 3;
            return 4;
          };
          const jsonHexDigits = "0123456789abcdef";
          const jsonString = (value, maximumUnits) => {
            let encoded = '"';
            let inspectedUnits = 0;
            for (const character of value) {
              inspectedUnits += character.length;
              if (inspectedUnits > maximumUnits) break;
              if (character === '"') {
                encoded += '\\"';
                continue;
              }
              if (character === "\\") {
                encoded += "\\\\";
                continue;
              }
              const codePoint = character.codePointAt(0);
              if (codePoint >= 0xd800 && codePoint <= 0xdfff) {
                encoded += "\\ufffd";
                continue;
              }
              if (codePoint <= 0x1f) {
                encoded += "\\u00"
                  + jsonHexDigits[(codePoint >> 4) & 0xf]
                  + jsonHexDigits[codePoint & 0xf];
                continue;
              }
              if (codePoint === 0x2028) {
                encoded += "\\u2028";
                continue;
              }
              if (codePoint === 0x2029) {
                encoded += "\\u2029";
                continue;
              }
              encoded += character;
            }
            return encoded + '"';
          };
          const serializeSnapshot = (
              nodes,
              truncated,
              mutationEpoch) => {
            let encoded = '{"snapshot_nonce":'
              + jsonString(snapshotNonce, 64)
              + ',"mutation_epoch":' + mutationEpoch
              + ',"nodes":[';
            const count = nodes.length < maximumNodes
              ? nodes.length
              : maximumNodes;
            for (let index = 0; index < count; index += 1) {
              const node = nodes[index];
              if (index > 0) encoded += ",";
              encoded += '{"depth":' + node.depth
                + ',"role":' + jsonString(node.role, 64)
                + ',"name":' + jsonString(node.name, 256)
                + ',"states":' + node.states
                + ',"handle":'
                + (node.handle === null
                  ? "null"
                  : jsonString(node.handle, 64))
                + "}";
            }
            return encoded + '],"truncated":'
              + (truncated ? "true" : "false")
              + "}";
          };
          const textState = maximumBytes => ({
            value: "",
            bytes: 0,
            maximumBytes,
            pendingSpace: false
          });
          const appendText = (state, value) => {
            if (typeof value !== "string" || value.length === 0) return true;
            let inspectedUnits = 0;
            for (const rawCharacter of value) {
              inspectedUnits += rawCharacter.length;
              if (inspectedUnits > maximumTextSourceUnits) break;
              let character = rawCharacter;
              let codePoint = character.codePointAt(0);
              if (codePoint >= 0xd800 && codePoint <= 0xdfff) {
                character = "\ufffd";
                codePoint = 0xfffd;
              }
              if (codePoint <= 0x1f
                  || codePoint === 0x7f
                  || whitespace.test(character)) {
                if (state.bytes > 0) state.pendingSpace = true;
                continue;
              }
              const characterBytes = utf8Width(codePoint);
              const requiredBytes =
                characterBytes + (state.pendingSpace ? 1 : 0);
              if (state.bytes + requiredBytes > state.maximumBytes) {
                return false;
              }
              if (state.pendingSpace) {
                state.value += " ";
                state.bytes += 1;
                state.pendingSpace = false;
              }
              state.value += character;
              state.bytes += characterBytes;
            }
            return state.bytes < state.maximumBytes;
          };
          const boundedText = (value, maximumBytes) => {
            const state = textState(maximumBytes);
            appendText(state, value);
            return state.value;
          };
          const isExcludedMarkup = element =>
            excludedTags.has(element.localName)
            || element.hasAttribute("hidden")
            || element.hasAttribute("inert")
            || element.getAttribute("aria-hidden") === "true";
          const subtreeText = (element, maximumBytes) => {
            const state = textState(maximumBytes);
            const first = element.firstChild;
            if (!first) return state.value;
            const stack = [first];
            let visited = 0;
            while (stack.length > 0
                   && visited < maximumTextNodesPerLabel
                   && remainingTextNodes > 0
                   && state.bytes < maximumBytes) {
              const node = stack.pop();
              if (!node) continue;
              visited += 1;
              remainingTextNodes -= 1;
              if (node.nextSibling) stack.push(node.nextSibling);
              if (node.nodeType === Node.TEXT_NODE) {
                appendText(state, node.nodeValue);
                continue;
              }
              if (node.nodeType !== Node.ELEMENT_NODE
                  || isExcludedMarkup(node)) {
                continue;
              }
              if (node.firstChild) stack.push(node.firstChild);
            }
            return state.value;
          };
          const directText = (element, maximumBytes) => {
            const state = textState(maximumBytes);
            let node = element.firstChild;
            let visited = 0;
            while (node
                   && visited < maximumDirectTextNodes
                   && remainingTextNodes > 0
                   && state.bytes < maximumBytes) {
              visited += 1;
              remainingTextNodes -= 1;
              if (node.nodeType === Node.TEXT_NODE) {
                appendText(state, node.nodeValue);
              }
              node = node.nextSibling;
            }
            return state.value;
          };
          const explicitRole = element => {
            const roles = boundedText(element.getAttribute("role"), 64)
              .toLowerCase();
            const separator = roles.indexOf(" ");
            const value = separator < 0 ? roles : roles.slice(0, separator);
            return /^[a-z][a-z0-9_-]{0,63}$/.test(value) ? value : "";
          };
          const implicitRole = element => {
            const tag = element.localName;
            if (/^h[1-6]$/.test(tag)) return "heading";
            if (tag === "a" && element.hasAttribute("href")) return "link";
            if (tag === "button") return "button";
            if (tag === "textarea") return "textbox";
            if (tag === "select") return "combobox";
            if (tag === "summary") return "button";
            if (tag === "img") return "img";
            if (tag === "nav") return "navigation";
            if (tag === "main") return "main";
            if (tag === "header") return "banner";
            if (tag === "footer") return "contentinfo";
            if (tag === "form") return "form";
            if (tag === "table") return "table";
            if (tag === "tr") return "row";
            if (tag === "th") return "columnheader";
            if (tag === "td") return "cell";
            if (tag === "ul" || tag === "ol") return "list";
            if (tag === "li") return "listitem";
            if (tag !== "input") return "";
            const type = boundedText(element.type || "text", 32).toLowerCase();
            if (type === "hidden") return "";
            if (type === "checkbox") return "checkbox";
            if (type === "radio") return "radio";
            if (type === "button" || type === "submit" || type === "reset") {
              return "button";
            }
            return "textbox";
          };
          const appendLabelledBy = (state, value) => {
            if (typeof value !== "string" || value.length === 0) return;
            let token = "";
            let tokenTooLong = false;
            let inspectedUnits = 0;
            let count = 0;
            const appendToken = () => {
              if (token.length === 0 || tokenTooLong) return;
              const label = document.getElementById(token);
              if (label) {
                appendText(
                  state,
                  subtreeText(label, state.maximumBytes - state.bytes));
              }
              count += 1;
            };
            for (const character of value) {
              inspectedUnits += character.length;
              if (inspectedUnits > maximumTextSourceUnits
                  || count >= maximumLabelledByIds
                  || state.bytes >= state.maximumBytes) {
                break;
              }
              if (whitespace.test(character)) {
                appendToken();
                token = "";
                tokenTooLong = false;
                continue;
              }
              if (token.length < 128) {
                token += character;
              } else {
                tokenTooLong = true;
              }
            }
            if (count < maximumLabelledByIds
                && state.bytes < state.maximumBytes) {
              appendToken();
            }
          };
          const labelledName = element => {
            const ariaLabel = boundedText(
              element.getAttribute("aria-label"),
              256);
            if (ariaLabel) return ariaLabel;
            const labelledBy = textState(256);
            appendLabelledBy(
              labelledBy,
              element.getAttribute("aria-labelledby"));
            if (labelledBy.value) return labelledBy.value;
            const labels = element.labels;
            const labelState = textState(256);
            const labelCount = labels
              ? Math.min(labels.length, maximumLabels)
              : 0;
            for (let index = 0;
                 index < labelCount
                 && labelState.bytes < labelState.maximumBytes;
                 index += 1) {
              const label = labels[index];
              if (label) {
                appendText(
                  labelState,
                  subtreeText(
                    label,
                    labelState.maximumBytes - labelState.bytes));
              }
            }
            if (labelState.value) return labelState.value;
            const attributes = ["alt", "title", "placeholder"];
            for (let index = 0; index < attributes.length; index += 1) {
              const value = boundedText(
                element.getAttribute(attributes[index]),
                256);
              if (value) return value;
            }
            return directText(element, 256);
          };
          const stateBits = element => {
            let bits = 0;
            if (element.disabled
                || element.getAttribute("aria-disabled") === "true") bits |= 1;
            if (element.checked
                || element.getAttribute("aria-checked") === "true") bits |= 2;
            if (element.selected
                || element.getAttribute("aria-selected") === "true") bits |= 4;
            if (element.getAttribute("aria-expanded") === "true") bits |= 8;
            if (element.getAttribute("aria-pressed") === "true") bits |= 16;
            if (element.required
                || element.getAttribute("aria-required") === "true") bits |= 32;
            if (element.readOnly
                || element.getAttribute("aria-readonly") === "true") bits |= 64;
            return bits;
          };
          const registryDescriptor =
            Object.getOwnPropertyDescriptor(window, registrySlot);
          let registry = registryDescriptor
            && registryDescriptor.value;
          if (!registryDescriptor) {
            const entries = new Map();
            const nativeClick = HTMLElement.prototype.click;
            const inputValueDescriptor =
              Object.getOwnPropertyDescriptor(
                HTMLInputElement.prototype,
                "value");
            const textareaValueDescriptor =
              Object.getOwnPropertyDescriptor(
                HTMLTextAreaElement.prototype,
                "value");
            const nativeInputValueSetter =
              inputValueDescriptor && inputValueDescriptor.set;
            const nativeInputValueGetter =
              inputValueDescriptor && inputValueDescriptor.get;
            const nativeTextareaValueSetter =
              textareaValueDescriptor && textareaValueDescriptor.set;
            const nativeTextareaValueGetter =
              textareaValueDescriptor && textareaValueDescriptor.get;
            const inputTypeDescriptor =
              Object.getOwnPropertyDescriptor(
                HTMLInputElement.prototype,
                "type");
            const nativeInputTypeGetter =
              inputTypeDescriptor && inputTypeDescriptor.get;
            const inputCheckedDescriptor =
              Object.getOwnPropertyDescriptor(
                HTMLInputElement.prototype,
                "checked");
            const nativeInputCheckedGetter =
              inputCheckedDescriptor && inputCheckedDescriptor.get;
            const inputMultipleDescriptor =
              Object.getOwnPropertyDescriptor(
                HTMLInputElement.prototype,
                "multiple");
            const nativeInputMultipleGetter =
              inputMultipleDescriptor && inputMultipleDescriptor.get;
            const nativeDispatchEvent =
              EventTarget.prototype.dispatchEvent;
            const nativeContains = Node.prototype.contains;
            const NativeEvent = Event;
            const fillableInputTypes = new Set([
              "text", "search", "email", "url", "tel"
            ]);
            const isAsciiWhitespace = character =>
              character === " "
              || character === "\t"
              || character === "\n"
              || character === "\r"
              || character === "\f";
            const containsLineBreak = value => {
              for (let index = 0; index < value.length; index += 1) {
                if (value[index] === "\n" || value[index] === "\r") {
                  return true;
                }
              }
              return false;
            };
            const containsCarriageReturn = value => {
              for (let index = 0; index < value.length; index += 1) {
                if (value[index] === "\r") return true;
              }
              return false;
            };
            const hasEdgeAsciiWhitespace = (
                value,
                start,
                end) =>
              start < end
              && (isAsciiWhitespace(value[start])
                || isAsciiWhitespace(value[end - 1]));
            const multipleEmailWouldNormalize = value => {
              let tokenStart = 0;
              for (let index = 0; index <= value.length; index += 1) {
                if (index !== value.length && value[index] !== ",") {
                  continue;
                }
                if (hasEdgeAsciiWhitespace(
                    value,
                    tokenStart,
                    index)) {
                  return true;
                }
                tokenStart = index + 1;
              }
              return false;
            };
            let currentNonce = "";
            let mutationEpoch = 0;
            const observer = new MutationObserver(records => {
              if (records.length > 0) mutationEpoch += 1;
            });
            observer.observe(document, {
              subtree: true,
              childList: true,
              attributes: true,
              characterData: true
            });
            const flushMutations = () => {
              const records = observer.takeRecords();
              if (records.length > 0) mutationEpoch += 1;
            };
            const isEffectivelyDisabled = element => {
              if (element.disabled) return true;
              let ancestor = element.parentElement;
              let depth = 0;
              while (ancestor && depth < 64) {
                if (ancestor.localName === "fieldset"
                    && ancestor.disabled) {
                  let firstLegend = ancestor.firstElementChild;
                  while (firstLegend
                         && firstLegend.localName !== "legend") {
                    firstLegend = firstLegend.nextElementSibling;
                  }
                  if (!firstLegend
                      || typeof nativeContains !== "function"
                      || !nativeContains.call(firstLegend, element)) {
                    return true;
                  }
                }
                ancestor = ancestor.parentElement;
                depth += 1;
              }
              return false;
            };
            const status = value => '{"status":"' + value + '"}';
            const api = {
              begin(secret, nonce) {
                if (secret !== registrySecret
                    || typeof nonce !== "string"
                    || !/^[A-Za-z0-9_-]{1,64}$/.test(nonce)) {
                  return -1;
                }
                flushMutations();
                entries.clear();
                currentNonce = nonce;
                return mutationEpoch;
              },
              store(secret, nonce, token, element, validate) {
                if (secret !== registrySecret
                    || nonce !== currentNonce
                    || typeof token !== "string"
                    || !/^[A-Za-z0-9_-]{1,64}$/.test(token)
                    || entries.size >= maximumNodes
                    || entries.has(token)
                    || !(element instanceof HTMLElement)
                    || typeof validate !== "function") {
                  return false;
                }
                entries.set(token, { element, validate });
                return true;
              },
              finish(secret, nonce, captureEpoch) {
                if (secret !== registrySecret
                    || nonce !== currentNonce
                    || !Number.isSafeInteger(captureEpoch)
                    || captureEpoch < 0) {
                  entries.clear();
                  currentNonce = "";
                  return -1;
                }
                flushMutations();
                if (captureEpoch !== mutationEpoch) {
                  entries.clear();
                  currentNonce = "";
                  return -1;
                }
                return mutationEpoch;
              },
              activate(secret, nonce, token, expectedEpoch) {
                if (secret !== registrySecret) return status("stale");
                flushMutations();
                if (nonce !== currentNonce
                    || !Number.isSafeInteger(expectedEpoch)
                    || expectedEpoch < 0
                    || expectedEpoch !== mutationEpoch) {
                  entries.clear();
                  currentNonce = "";
                  return status("stale");
                }
                const entry = entries.get(token);
                entries.clear();
                currentNonce = "";
                if (!entry) return status("stale");
                let validation;
                try {
                  validation = entry.validate();
                } catch {
                  return status("stale");
                }
                if (validation === "stale") return status("stale");
                if (validation !== "ready") {
                  return status("not_interactable");
                }
                flushMutations();
                if (expectedEpoch !== mutationEpoch) {
                  return status("stale");
                }
                try {
                  nativeClick.call(entry.element);
                  return status("activated");
                } catch {
                  return status("outcome_unknown");
                }
              },
              fill(secret, nonce, token, expectedEpoch, text) {
                if (secret !== registrySecret
                    || typeof text !== "string") {
                  return status("stale");
                }
                flushMutations();
                if (nonce !== currentNonce
                    || !Number.isSafeInteger(expectedEpoch)
                    || expectedEpoch < 0
                    || expectedEpoch !== mutationEpoch) {
                  entries.clear();
                  currentNonce = "";
                  return status("stale");
                }
                const entry = entries.get(token);
                entries.clear();
                currentNonce = "";
                if (!entry) return status("stale");
                let validation;
                try {
                  validation = entry.validate();
                } catch {
                  return status("stale");
                }
                if (validation === "stale") return status("stale");
                if (validation !== "ready") {
                  return status("not_interactable");
                }
                const element = entry.element;
                let nativeValueSetter;
                let nativeValueGetter;
                let inputType = "";
                if (element instanceof HTMLTextAreaElement) {
                  nativeValueSetter = nativeTextareaValueSetter;
                  nativeValueGetter = nativeTextareaValueGetter;
                } else if (element instanceof HTMLInputElement) {
                  if (typeof nativeInputTypeGetter !== "function") {
                    return status("outcome_unknown");
                  }
                  inputType = String(
                    nativeInputTypeGetter.call(element) || "text")
                    .toLowerCase();
                  if (!fillableInputTypes.has(inputType)) {
                    return status("not_fillable");
                  }
                  nativeValueSetter = nativeInputValueSetter;
                  nativeValueGetter = nativeInputValueGetter;
                } else {
                  return status("not_fillable");
                }
                if (element.readOnly
                    || element.getAttribute("aria-readonly") === "true"
                    || typeof nativeValueSetter !== "function"
                    || typeof nativeValueGetter !== "function"
                    || typeof nativeDispatchEvent !== "function"
                    || typeof NativeEvent !== "function") {
                  return element.readOnly
                      || element.getAttribute("aria-readonly") === "true"
                    ? status("not_interactable")
                    : status("outcome_unknown");
                }
                if (inputType) {
                  if (containsLineBreak(text)) {
                    return status("value_not_supported");
                  }
                  if (inputType === "url"
                      && hasEdgeAsciiWhitespace(
                        text,
                        0,
                        text.length)) {
                    return status("value_not_supported");
                  }
                  if (inputType === "email") {
                    if (typeof nativeInputMultipleGetter !== "function") {
                      return status("outcome_unknown");
                    }
                    const isMultiple =
                      nativeInputMultipleGetter.call(element);
                    if ((!isMultiple
                         && hasEdgeAsciiWhitespace(
                           text,
                           0,
                           text.length))
                        || (isMultiple
                          && multipleEmailWouldNormalize(text))) {
                      return status("value_not_supported");
                    }
                  }
                } else if (containsCarriageReturn(text)) {
                  return status("value_not_supported");
                }
                flushMutations();
                if (expectedEpoch !== mutationEpoch) {
                  return status("stale");
                }
                try {
                  nativeValueSetter.call(element, text);
                  if (nativeValueGetter.call(element) !== text) {
                    return status("outcome_unknown");
                  }
                  nativeDispatchEvent.call(
                    element,
                    new NativeEvent("input", {
                      bubbles: true,
                      composed: true
                    }));
                  if (nativeValueGetter.call(element) !== text) {
                    return status("outcome_unknown");
                  }
                  return status("filled");
                } catch {
                  return status("outcome_unknown");
                }
              },
              check(secret, nonce, token, expectedEpoch) {
                if (secret !== registrySecret) return status("stale");
                flushMutations();
                if (nonce !== currentNonce
                    || !Number.isSafeInteger(expectedEpoch)
                    || expectedEpoch < 0
                    || expectedEpoch !== mutationEpoch) {
                  entries.clear();
                  currentNonce = "";
                  return status("stale");
                }
                const entry = entries.get(token);
                entries.clear();
                currentNonce = "";
                if (!entry) return status("stale");
                let validation;
                try {
                  validation = entry.validate();
                } catch {
                  return status("stale");
                }
                if (validation === "stale") return status("stale");
                if (validation !== "ready") {
                  return status("not_interactable");
                }
                const element = entry.element;
                if (!(element instanceof HTMLInputElement)) {
                  return status("not_checkable");
                }
                if (typeof nativeInputTypeGetter !== "function"
                    || typeof nativeInputCheckedGetter !== "function") {
                  return status("outcome_unknown");
                }
                let inputType;
                let checked;
                try {
                  inputType = String(
                    nativeInputTypeGetter.call(element) || "text")
                    .toLowerCase();
                  if (inputType !== "checkbox"
                      && inputType !== "radio") {
                    return status("not_checkable");
                  }
                  checked = nativeInputCheckedGetter.call(element);
                } catch {
                  return status("outcome_unknown");
                }
                flushMutations();
                if (expectedEpoch !== mutationEpoch) {
                  return status("stale");
                }
                if (checked === true) return status("checked");
                if (checked !== false
                    || typeof nativeClick !== "function") {
                  return status("outcome_unknown");
                }
                try {
                  nativeClick.call(element);
                  return nativeInputCheckedGetter.call(element) === true
                    ? status("checked")
                    : status("outcome_unknown");
                } catch {
                  return status("outcome_unknown");
                }
              }
            };
            Object.freeze(api);
            Object.defineProperty(window, registrySlot, {
              value: api,
              configurable: false,
              enumerable: false,
              writable: false
            });
            registry = api;
          }
          if (!registry
              || typeof registry.begin !== "function"
              || typeof registry.store !== "function"
              || typeof registry.finish !== "function"
              || typeof registry.activate !== "function"
              || typeof registry.fill !== "function"
              || typeof registry.check !== "function") {
            return serializeSnapshot([], true, -1);
          }
          const captureEpoch =
            registry.begin(registrySecret, snapshotNonce);
          if (!Number.isSafeInteger(captureEpoch)
              || captureEpoch < 0) {
            return serializeSnapshot([], true, -1);
          }
          const root = document.documentElement;
          if (!root) {
            return serializeSnapshot([], false, captureEpoch);
          }
          const nodes = [{
            depth: 0,
            role: "document",
            name: boundedText(document.title, 256),
            states: 0,
            handle: null
          }];
          const first = root.firstElementChild;
          const frames = first
            ? [{
                next: first,
                includedDepth: 0,
                domDepth: 1
              }]
            : [];
          let visitedElements = 0;
          let truncated = false;
          while (frames.length > 0) {
            if (visitedElements >= maximumVisitedElements
                || nodes.length >= maximumNodes) {
              truncated = true;
              break;
            }
            const frame = frames[frames.length - 1];
            const element = frame.next;
            if (!element) {
              frames.pop();
              continue;
            }
            frame.next = element.nextElementSibling;
            visitedElements += 1;
            let excluded = isExcludedMarkup(element);
            if (!excluded) {
              const style = window.getComputedStyle(element);
              excluded = style.display === "none"
                || style.visibility === "hidden";
            }
            let includedDepth = frame.includedDepth;
            if (!excluded) {
              const role = explicitRole(element) || implicitRole(element);
              const name = labelledName(element);
              const actionable =
                element instanceof HTMLElement
                && (actionableRoles.has(role) || element.isContentEditable);
              if (role || name || actionable) {
                includedDepth = Math.min(
                  maximumDepth,
                  frame.includedDepth + 1);
                let handle = null;
                if (actionable) {
                  const token = "e" + String(nodes.length);
                  const expectedNamespace = element.namespaceURI || "";
                  const expectedTag = element.localName || "";
                  const expectedType = boundedText(
                    element.type || "",
                    32).toLowerCase();
                  const expectedRole = role;
                  const expectedName = name;
                  const expectedStates = stateBits(element);
                  const validate = () => {
                    if (element.ownerDocument !== document
                        || !element.isConnected
                        || !document.documentElement
                        || !document.documentElement.contains(element)
                        || (element.namespaceURI || "") !== expectedNamespace
                        || (element.localName || "") !== expectedTag
                        || boundedText(
                          element.type || "",
                          32).toLowerCase() !== expectedType
                        || (explicitRole(element) || implicitRole(element))
                          !== expectedRole
                        || labelledName(element) !== expectedName
                        || stateBits(element) !== expectedStates) {
                      return "stale";
                    }
                    let ancestor = element;
                    let ancestorDepth = 0;
                    while (ancestor && ancestorDepth < 64) {
                      if (isExcludedMarkup(ancestor)) {
                        return "not_interactable";
                      }
                      const ancestorStyle =
                        window.getComputedStyle(ancestor);
                      if (ancestorStyle.display === "none"
                          || ancestorStyle.visibility === "hidden") {
                        return "not_interactable";
                      }
                      if (ancestor === document.documentElement) break;
                      ancestor = ancestor.parentElement;
                      ancestorDepth += 1;
                    }
                    if (ancestor !== document.documentElement
                        || isEffectivelyDisabled(element)
                        || element.getAttribute("aria-disabled") === "true") {
                      return "not_interactable";
                    }
                    return "ready";
                  };
                  if (registry.store(
                      registrySecret,
                      snapshotNonce,
                      token,
                      element,
                      validate)) {
                    handle = token;
                  }
                }
                nodes.push({
                  depth: includedDepth,
                  role: role || "text",
                  name,
                  states: stateBits(element),
                  handle
                });
              }
            }
            const child = excluded ? null : element.firstElementChild;
            if (!child) continue;
            if (frame.domDepth >= maximumTraversalDepth) {
              truncated = true;
              continue;
            }
            frames.push({
              next: child,
              includedDepth,
              domDepth: frame.domDepth + 1
            });
          }
          const finishedEpoch = registry.finish(
            registrySecret,
            snapshotNonce,
            captureEpoch);
          return serializeSnapshot(
            finishedEpoch < 0 ? [] : nodes,
            truncated || finishedEpoch < 0,
            finishedEpoch);
        })()
        """;

    private const string ClickScriptTemplate = """
        (() => {
          const registrySlot = __GHOSTSHELL_REGISTRY_SLOT__;
          const registrySecret = __GHOSTSHELL_REGISTRY_SECRET__;
          const snapshotNonce = __GHOSTSHELL_SNAPSHOT_NONCE__;
          const elementToken = __GHOSTSHELL_ELEMENT_TOKEN__;
          const mutationEpoch = __GHOSTSHELL_MUTATION_EPOCH__;
          const registry = window[registrySlot];
          if (!registry || typeof registry.activate !== "function") {
            return '{"status":"stale"}';
          }
          return registry.activate(
            registrySecret,
            snapshotNonce,
            elementToken,
            mutationEpoch);
        })()
        """;

    private const string FillScriptTemplate = """
        (() => {
          const registrySlot = __GHOSTSHELL_REGISTRY_SLOT__;
          const registrySecret = __GHOSTSHELL_REGISTRY_SECRET__;
          const snapshotNonce = __GHOSTSHELL_SNAPSHOT_NONCE__;
          const elementToken = __GHOSTSHELL_ELEMENT_TOKEN__;
          const mutationEpoch = __GHOSTSHELL_MUTATION_EPOCH__;
          const text = __GHOSTSHELL_FILL_TEXT__;
          const registry = window[registrySlot];
          if (!registry || typeof registry.fill !== "function") {
            return '{"status":"stale"}';
          }
          return registry.fill(
            registrySecret,
            snapshotNonce,
            elementToken,
            mutationEpoch,
            text);
        })()
        """;

    private const string CheckScriptTemplate = """
        (() => {
          const registrySlot = __GHOSTSHELL_REGISTRY_SLOT__;
          const registrySecret = __GHOSTSHELL_REGISTRY_SECRET__;
          const snapshotNonce = __GHOSTSHELL_SNAPSHOT_NONCE__;
          const elementToken = __GHOSTSHELL_ELEMENT_TOKEN__;
          const mutationEpoch = __GHOSTSHELL_MUTATION_EPOCH__;
          const registry = window[registrySlot];
          if (!registry || typeof registry.check !== "function") {
            return '{"status":"stale"}';
          }
          return registry.check(
            registrySecret,
            snapshotNonce,
            elementToken,
            mutationEpoch);
        })()
        """;

    internal static string SnapshotScriptForTests =>
        CreateSnapshotScript(
            "ghostshell_registry_test",
            "ghostshell_secret_test",
            "ghostshell_snapshot_test");

    internal static string ClickScriptForTests =>
        CreateClickScript(
            "ghostshell_registry_test",
            "ghostshell_secret_test",
            new NativeBrowserElementHandle(
                "ghostshell_snapshot_test",
                "ghostshell_element_test",
                0));

    internal static string FillScriptForTests(string text) =>
        CreateFillScript(
            "ghostshell_registry_test",
            "ghostshell_secret_test",
            new NativeBrowserElementHandle(
                "ghostshell_snapshot_test",
                "ghostshell_element_test",
                0),
            text);

    internal static string CheckScriptForTests =>
        CreateCheckScript(
            "ghostshell_registry_test",
            "ghostshell_secret_test",
            new NativeBrowserElementHandle(
                "ghostshell_snapshot_test",
                "ghostshell_element_test",
                0));

    private readonly NativeWebView _webView;
    private readonly string _registrySlot;
    private readonly string _registrySecret;
    private ActiveNativeNavigation? _activeNavigation;
    private long _lastNavigationGeneration;

    public AvaloniaNativeBrowserView()
    {
        _registrySlot = string.Concat(
            "__ghostshell_",
            NewPrivateIdentifier());
        _registrySecret = NewPrivateIdentifier();
        _webView = new NativeWebView();
        _webView.EnvironmentRequested += OnEnvironmentRequested;
        _webView.NavigationStarted += OnNavigationStarted;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.NewWindowRequested += OnNewWindowRequested;
    }

    public Control View => _webView;

    public bool CanGoBack => _webView.CanGoBack;

    public bool CanGoForward => _webView.CanGoForward;

    public event EventHandler<NativeBrowserNavigationEventArgs>? NavigationStarted;

    public event EventHandler<NativeBrowserNavigationCompletedEventArgs>? NavigationCompleted;

    public event EventHandler<NativeBrowserNavigationRejectedEventArgs>?
        NavigationRejected;

    public void Navigate(BrowserAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var navigation = BeginExplicitNavigation(address);
        try
        {
            _webView.Navigate(address.Value);
        }
        catch
        {
            ClearUnstartedNavigation(navigation);
            throw;
        }
    }

    public bool GoBack() => InvokeExplicitNavigation(_webView.GoBack);

    public bool GoForward() => InvokeExplicitNavigation(_webView.GoForward);

    public bool Reload() => InvokeExplicitNavigation(_webView.Refresh);

    public bool Stop() => _webView.Stop();

    public async Task<NativeBrowserSnapshotResult> CaptureSnapshotAsync()
    {
        var snapshotNonce = NewPrivateIdentifier();
        string? raw;
        try
        {
            raw = await _webView.InvokeScript(
                    CreateSnapshotScript(
                        _registrySlot,
                        _registrySecret,
                        snapshotNonce))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return NativeBrowserSnapshotResult.Unavailable();
        }

        return ParseSnapshot(raw, snapshotNonce);
    }

    public async Task<NativeBrowserClickResult> ClickAsync(
        NativeBrowserElementHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        string? raw;
        try
        {
            raw = await _webView.InvokeScript(
                    CreateClickScript(
                        _registrySlot,
                        _registrySecret,
                        handle))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return NativeBrowserClickResult.OutcomeUnknown();
        }

        return ParseClick(raw);
    }

    public async Task<NativeBrowserFillResult> FillAsync(
        NativeBrowserElementHandle handle,
        string text)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(text);
        string? raw;
        try
        {
            raw = await _webView.InvokeScript(
                    CreateFillScript(
                        _registrySlot,
                        _registrySecret,
                        handle,
                        text))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return NativeBrowserFillResult.OutcomeUnknown();
        }

        return ParseFill(raw);
    }

    public async Task<NativeBrowserCheckResult> CheckAsync(
        NativeBrowserElementHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        string? raw;
        try
        {
            raw = await _webView.InvokeScript(
                    CreateCheckScript(
                        _registrySlot,
                        _registrySecret,
                        handle))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return NativeBrowserCheckResult.OutcomeUnknown();
        }

        return ParseCheck(raw);
    }

    private static string CreateSnapshotScript(
        string registrySlot,
        string registrySecret,
        string snapshotNonce) =>
        SnapshotScriptTemplate
            .Replace(
                "__GHOSTSHELL_REGISTRY_SLOT__",
                JsonLiteral(RequirePrivateIdentifier(registrySlot)),
                StringComparison.Ordinal)
            .Replace(
                "__GHOSTSHELL_REGISTRY_SECRET__",
                JsonLiteral(RequirePrivateIdentifier(registrySecret)),
                StringComparison.Ordinal)
            .Replace(
                "__GHOSTSHELL_SNAPSHOT_NONCE__",
                JsonLiteral(RequirePrivateIdentifier(snapshotNonce)),
                StringComparison.Ordinal);

    private static string CreateClickScript(
        string registrySlot,
        string registrySecret,
        NativeBrowserElementHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return ClickScriptTemplate
            .Replace(
                "__GHOSTSHELL_REGISTRY_SLOT__",
                JsonLiteral(RequirePrivateIdentifier(registrySlot)),
                StringComparison.Ordinal)
            .Replace(
                "__GHOSTSHELL_REGISTRY_SECRET__",
                JsonLiteral(RequirePrivateIdentifier(registrySecret)),
                StringComparison.Ordinal)
            .Replace(
                "__GHOSTSHELL_SNAPSHOT_NONCE__",
                JsonLiteral(handle.SnapshotNonce),
                StringComparison.Ordinal)
            .Replace(
                "__GHOSTSHELL_ELEMENT_TOKEN__",
                JsonLiteral(handle.ElementToken),
                StringComparison.Ordinal)
            .Replace(
                "__GHOSTSHELL_MUTATION_EPOCH__",
                handle.MutationEpoch.ToString(
                    CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }

    private static string CreateFillScript(
        string registrySlot,
        string registrySecret,
        NativeBrowserElementHandle handle,
        string text)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(text);
        return FillScriptTemplate
            .Replace(
                "__GHOSTSHELL_REGISTRY_SLOT__",
                JsonLiteral(RequirePrivateIdentifier(registrySlot)),
                StringComparison.Ordinal)
            .Replace(
                "__GHOSTSHELL_REGISTRY_SECRET__",
                JsonLiteral(RequirePrivateIdentifier(registrySecret)),
                StringComparison.Ordinal)
            .Replace(
                "__GHOSTSHELL_SNAPSHOT_NONCE__",
                JsonLiteral(handle.SnapshotNonce),
                StringComparison.Ordinal)
            .Replace(
                "__GHOSTSHELL_ELEMENT_TOKEN__",
                JsonLiteral(handle.ElementToken),
                StringComparison.Ordinal)
            .Replace(
                "__GHOSTSHELL_MUTATION_EPOCH__",
                handle.MutationEpoch.ToString(
                    CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace(
                "__GHOSTSHELL_FILL_TEXT__",
                JsonLiteral(text),
                StringComparison.Ordinal);
    }

    private static string CreateCheckScript(
        string registrySlot,
        string registrySecret,
        NativeBrowserElementHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return CheckScriptTemplate
            .Replace(
                "__GHOSTSHELL_REGISTRY_SLOT__",
                JsonLiteral(RequirePrivateIdentifier(registrySlot)),
                StringComparison.Ordinal)
            .Replace(
                "__GHOSTSHELL_REGISTRY_SECRET__",
                JsonLiteral(RequirePrivateIdentifier(registrySecret)),
                StringComparison.Ordinal)
            .Replace(
                "__GHOSTSHELL_SNAPSHOT_NONCE__",
                JsonLiteral(handle.SnapshotNonce),
                StringComparison.Ordinal)
            .Replace(
                "__GHOSTSHELL_ELEMENT_TOKEN__",
                JsonLiteral(handle.ElementToken),
                StringComparison.Ordinal)
            .Replace(
                "__GHOSTSHELL_MUTATION_EPOCH__",
                handle.MutationEpoch.ToString(
                    CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }

    private static string NewPrivateIdentifier() =>
        Convert.ToBase64String(
                RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string RequirePrivateIdentifier(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 96
            || value.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_'))
        {
            throw new ArgumentException(
                "A private browser-script identifier is invalid.",
                nameof(value));
        }

        return value;
    }

    private static string JsonLiteral(string value) =>
        JsonSerializer.Serialize(value);

    private static void OnEnvironmentRequested(
        object? sender,
        WebViewEnvironmentRequestedEventArgs args)
    {
        // Developer tools are intentionally unavailable in the first browser
        // slice. Durable profiles and explicit private sessions belong to a
        // later, typed profile contract rather than implicit platform switches.
        args.EnableDevTools = false;
    }

    private void OnNavigationStarted(
        object? sender,
        WebViewNavigationStartingEventArgs args)
    {
        var navigation = EnsureActiveNavigation();
        navigation.HasStarted = true;
        if (!TryGetAddress(args.Request, out var address))
        {
            navigation.PendingAddress = null;
            args.Cancel = true;
            NavigationRejected?.Invoke(
                this,
                new NativeBrowserNavigationRejectedEventArgs(
                    NativeBrowserNavigationRejectionReason.UnsupportedAddress,
                    navigation.Generation));
            return;
        }

        navigation.PendingAddress = address;
        var starting = new NativeBrowserNavigationEventArgs(
            address,
            navigation.Generation);
        NavigationStarted?.Invoke(this, starting);
        if (starting.Cancel)
        {
            args.Cancel = true;
            NavigationRejected?.Invoke(
                this,
                new NativeBrowserNavigationRejectedEventArgs(
                    NativeBrowserNavigationRejectionReason.OriginPolicy,
                    navigation.Generation));
        }
    }

    private void OnNavigationCompleted(
        object? sender,
        WebViewNavigationCompletedEventArgs args)
    {
        var navigation = _activeNavigation;
        if (navigation is null)
        {
            // A completion without an active local generation is a duplicate
            // or otherwise uncorrelatable vendor event. It cannot update the
            // GhostSHELL browser state.
            return;
        }

        if (!TryGetAddress(args.Request, out var address))
        {
            address = navigation.PendingAddress;
        }

        _activeNavigation = null;
        NavigationCompleted?.Invoke(
            this,
            new NativeBrowserNavigationCompletedEventArgs(
                address,
                args.IsSuccess && address is not null,
                navigation.Generation));
    }

    private bool InvokeExplicitNavigation(Func<bool> operation)
    {
        var navigation = BeginExplicitNavigation(pendingAddress: null);
        try
        {
            var accepted = operation();
            if (!accepted)
            {
                ClearUnstartedNavigation(navigation);
            }

            return accepted;
        }
        catch
        {
            ClearUnstartedNavigation(navigation);
            throw;
        }
    }

    private ActiveNativeNavigation BeginExplicitNavigation(
        BrowserAddress? pendingAddress)
    {
        if (_activeNavigation is not null)
        {
            throw new InvalidOperationException(
                "A native top-level navigation is already active.");
        }

        return _activeNavigation = NewNavigation(pendingAddress);
    }

    private ActiveNativeNavigation EnsureActiveNavigation() =>
        _activeNavigation ??= NewNavigation(pendingAddress: null);

    private ActiveNativeNavigation NewNavigation(
        BrowserAddress? pendingAddress)
    {
        _lastNavigationGeneration = checked(
            _lastNavigationGeneration + 1);
        return new ActiveNativeNavigation(
            _lastNavigationGeneration,
            pendingAddress);
    }

    private void ClearUnstartedNavigation(
        ActiveNativeNavigation navigation)
    {
        if (ReferenceEquals(_activeNavigation, navigation)
            && !navigation.HasStarted)
        {
            _activeNavigation = null;
        }
    }

    private static void OnNewWindowRequested(
        object? sender,
        WebViewNewWindowRequestedEventArgs args)
    {
        // A remote page cannot create an ungoverned browser surface. A future
        // user-visible open-in-new-panel action must cross the host boundary.
        args.Handled = true;
    }

    private static bool TryGetAddress(
        Uri? request,
        [NotNullWhen(true)] out BrowserAddress? address) =>
        BrowserAddress.TryParse(request?.AbsoluteUri, out address);

    internal static NativeBrowserSnapshotResult ParseSnapshot(
        string? raw,
        string expectedSnapshotNonce)
    {
        if (!IsHandleIdentifier(
                expectedSnapshotNonce,
                NativeBrowserElementHandle.MaximumNonceLength))
        {
            throw new ArgumentException(
                "The expected snapshot nonce is invalid.",
                nameof(expectedSnapshotNonce));
        }

        if (string.IsNullOrWhiteSpace(raw)
            || !TryGetUtf8ByteCount(raw, out var rawBytes)
            || rawBytes > MaximumNativeSnapshotBytes)
        {
            return NativeBrowserSnapshotResult.Invalid();
        }

        try
        {
            using var envelope = JsonDocument.Parse(
                raw,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumSnapshotDepth + 8,
                });
            if (envelope.RootElement.ValueKind == JsonValueKind.String)
            {
                var decoded = envelope.RootElement.GetString();
                if (string.IsNullOrWhiteSpace(decoded)
                    || !TryGetUtf8ByteCount(decoded, out var decodedBytes)
                    || decodedBytes > MaximumNativeSnapshotBytes)
                {
                    return NativeBrowserSnapshotResult.Invalid();
                }

                using var payload = JsonDocument.Parse(
                    decoded,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = MaximumSnapshotDepth + 8,
                    });
                return ParseSnapshotPayload(
                    payload.RootElement,
                    expectedSnapshotNonce);
            }

            return ParseSnapshotPayload(
                envelope.RootElement,
                expectedSnapshotNonce);
        }
        catch (JsonException)
        {
            return NativeBrowserSnapshotResult.Invalid();
        }
        catch (InvalidOperationException)
        {
            return NativeBrowserSnapshotResult.Invalid();
        }
    }

    private static NativeBrowserSnapshotResult ParseSnapshotPayload(
        JsonElement root,
        string expectedSnapshotNonce)
    {
        if (!TryReadUniqueProperties(root, out var properties)
            || properties.Count != 4
            || !properties.TryGetValue(
                "snapshot_nonce",
                out var nonceElement)
            || nonceElement.ValueKind != JsonValueKind.String
            || nonceElement.GetString() is not { } nonce
            || !string.Equals(
                nonce,
                expectedSnapshotNonce,
                StringComparison.Ordinal)
            || !properties.TryGetValue(
                "mutation_epoch",
                out var mutationEpochElement)
            || !mutationEpochElement.TryGetInt64(
                out var mutationEpoch)
            || mutationEpoch is < 0
                or > NativeBrowserElementHandle.MaximumMutationEpoch
            || !properties.TryGetValue("nodes", out var nodesElement)
            || nodesElement.ValueKind != JsonValueKind.Array
            || !properties.TryGetValue("truncated", out var truncatedElement)
            || truncatedElement.ValueKind is not JsonValueKind.True
                and not JsonValueKind.False)
        {
            return NativeBrowserSnapshotResult.Invalid();
        }

        var nodes = new List<NativeBrowserSnapshotNode>();
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in nodesElement.EnumerateArray())
        {
            if (nodes.Count >= MaximumSnapshotNodes
                || !TryParseSnapshotNode(
                    element,
                    expectedSnapshotNonce,
                    mutationEpoch,
                    tokens,
                    out var node))
            {
                return NativeBrowserSnapshotResult.Invalid();
            }

            nodes.Add(node);
        }

        if (nodes.Count == 0
            || nodes[0].Depth != 0
            || nodes[0].Handle is not null)
        {
            return NativeBrowserSnapshotResult.Invalid();
        }

        return NativeBrowserSnapshotResult.Success(
            new NativeBrowserSnapshot(
                nodes.AsReadOnly(),
                truncatedElement.GetBoolean()));
    }

    private static bool TryParseSnapshotNode(
        JsonElement element,
        string snapshotNonce,
        long mutationEpoch,
        HashSet<string> tokens,
        [NotNullWhen(true)] out NativeBrowserSnapshotNode? node)
    {
        node = null;
        if (!TryReadUniqueProperties(element, out var properties)
            || properties.Count != 5
            || !properties.TryGetValue("depth", out var depthElement)
            || !depthElement.TryGetInt32(out var depth)
            || depth is < 0 or > MaximumSnapshotDepth
            || !properties.TryGetValue("role", out var roleElement)
            || roleElement.ValueKind != JsonValueKind.String
            || roleElement.GetString() is not { } role
            || !IsRole(role)
            || !properties.TryGetValue("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || nameElement.GetString() is not { } name
            || !IsBoundedText(name, MaximumNameBytes, allowEmpty: true)
            || !properties.TryGetValue("states", out var statesElement)
            || !statesElement.TryGetInt32(out var statesValue)
            || statesValue is < 0 or > 127
            || !properties.TryGetValue("handle", out var handleElement))
        {
            return false;
        }

        NativeBrowserElementHandle? handle = null;
        if (handleElement.ValueKind == JsonValueKind.String)
        {
            var token = handleElement.GetString();
            if (token is null
                || !IsHandleIdentifier(
                    token,
                    NativeBrowserElementHandle.MaximumTokenLength)
                || !tokens.Add(token))
            {
                return false;
            }

            handle = new NativeBrowserElementHandle(
                snapshotNonce,
                token,
                mutationEpoch);
        }
        else if (handleElement.ValueKind != JsonValueKind.Null)
        {
            return false;
        }

        node = new NativeBrowserSnapshotNode(
            depth,
            role,
            name,
            (BrowserSnapshotNodeState)statesValue,
            handle);
        return true;
    }

    internal static NativeBrowserClickResult ParseClick(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || !TryGetUtf8ByteCount(raw, out var rawBytes)
            || rawBytes > MaximumNativeClickResultBytes)
        {
            return NativeBrowserClickResult.OutcomeUnknown();
        }

        try
        {
            using var envelope = JsonDocument.Parse(
                raw,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            if (envelope.RootElement.ValueKind != JsonValueKind.String)
            {
                return ParseClickPayload(envelope.RootElement);
            }

            var decoded = envelope.RootElement.GetString();
            if (string.IsNullOrWhiteSpace(decoded)
                || !TryGetUtf8ByteCount(
                    decoded,
                    out var decodedBytes)
                || decodedBytes > MaximumNativeClickResultBytes)
            {
                return NativeBrowserClickResult.OutcomeUnknown();
            }

            using var payload = JsonDocument.Parse(
                decoded,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            return ParseClickPayload(payload.RootElement);
        }
        catch (JsonException)
        {
            return NativeBrowserClickResult.OutcomeUnknown();
        }
        catch (InvalidOperationException)
        {
            return NativeBrowserClickResult.OutcomeUnknown();
        }
    }

    private static NativeBrowserClickResult ParseClickPayload(
        JsonElement root)
    {
        if (!TryReadUniqueProperties(root, out var properties)
            || properties.Count != 1
            || !properties.TryGetValue("status", out var statusElement)
            || statusElement.ValueKind != JsonValueKind.String)
        {
            return NativeBrowserClickResult.OutcomeUnknown();
        }

        return statusElement.GetString() switch
        {
            "activated" => NativeBrowserClickResult.Activated(),
            "stale" => NativeBrowserClickResult.Stale(),
            "not_interactable" =>
                NativeBrowserClickResult.NotInteractable(),
            "outcome_unknown" =>
                NativeBrowserClickResult.OutcomeUnknown(),
            _ => NativeBrowserClickResult.OutcomeUnknown(),
        };
    }

    internal static NativeBrowserFillResult ParseFill(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || !TryGetUtf8ByteCount(raw, out var rawBytes)
            || rawBytes > MaximumNativeFillResultBytes)
        {
            return NativeBrowserFillResult.OutcomeUnknown();
        }

        try
        {
            using var envelope = JsonDocument.Parse(
                raw,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            if (envelope.RootElement.ValueKind != JsonValueKind.String)
            {
                return ParseFillPayload(envelope.RootElement);
            }

            var decoded = envelope.RootElement.GetString();
            if (string.IsNullOrWhiteSpace(decoded)
                || !TryGetUtf8ByteCount(
                    decoded,
                    out var decodedBytes)
                || decodedBytes > MaximumNativeFillResultBytes)
            {
                return NativeBrowserFillResult.OutcomeUnknown();
            }

            using var payload = JsonDocument.Parse(
                decoded,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            return ParseFillPayload(payload.RootElement);
        }
        catch (JsonException)
        {
            return NativeBrowserFillResult.OutcomeUnknown();
        }
        catch (InvalidOperationException)
        {
            return NativeBrowserFillResult.OutcomeUnknown();
        }
    }

    private static NativeBrowserFillResult ParseFillPayload(
        JsonElement root)
    {
        if (!TryReadUniqueProperties(root, out var properties)
            || properties.Count != 1
            || !properties.TryGetValue("status", out var statusElement)
            || statusElement.ValueKind != JsonValueKind.String)
        {
            return NativeBrowserFillResult.OutcomeUnknown();
        }

        return statusElement.GetString() switch
        {
            "filled" => NativeBrowserFillResult.Filled(),
            "stale" => NativeBrowserFillResult.Stale(),
            "not_interactable" =>
                NativeBrowserFillResult.NotInteractable(),
            "not_fillable" =>
                NativeBrowserFillResult.NotFillable(),
            "value_not_supported" =>
                NativeBrowserFillResult.ValueNotSupported(),
            "outcome_unknown" =>
                NativeBrowserFillResult.OutcomeUnknown(),
            _ => NativeBrowserFillResult.OutcomeUnknown(),
        };
    }

    internal static NativeBrowserCheckResult ParseCheck(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || !TryGetUtf8ByteCount(raw, out var rawBytes)
            || rawBytes > MaximumNativeCheckResultBytes)
        {
            return NativeBrowserCheckResult.OutcomeUnknown();
        }

        try
        {
            using var envelope = JsonDocument.Parse(
                raw,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            if (envelope.RootElement.ValueKind != JsonValueKind.String)
            {
                return ParseCheckPayload(envelope.RootElement);
            }

            var decoded = envelope.RootElement.GetString();
            if (string.IsNullOrWhiteSpace(decoded)
                || !TryGetUtf8ByteCount(
                    decoded,
                    out var decodedBytes)
                || decodedBytes > MaximumNativeCheckResultBytes)
            {
                return NativeBrowserCheckResult.OutcomeUnknown();
            }

            using var payload = JsonDocument.Parse(
                decoded,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            return ParseCheckPayload(payload.RootElement);
        }
        catch (JsonException)
        {
            return NativeBrowserCheckResult.OutcomeUnknown();
        }
        catch (InvalidOperationException)
        {
            return NativeBrowserCheckResult.OutcomeUnknown();
        }
    }

    private static NativeBrowserCheckResult ParseCheckPayload(
        JsonElement root)
    {
        if (!TryReadUniqueProperties(root, out var properties)
            || properties.Count != 1
            || !properties.TryGetValue("status", out var statusElement)
            || statusElement.ValueKind != JsonValueKind.String)
        {
            return NativeBrowserCheckResult.OutcomeUnknown();
        }

        return statusElement.GetString() switch
        {
            "checked" => NativeBrowserCheckResult.Checked(),
            "stale" => NativeBrowserCheckResult.Stale(),
            "not_interactable" =>
                NativeBrowserCheckResult.NotInteractable(),
            "not_checkable" =>
                NativeBrowserCheckResult.NotCheckable(),
            "outcome_unknown" =>
                NativeBrowserCheckResult.OutcomeUnknown(),
            _ => NativeBrowserCheckResult.OutcomeUnknown(),
        };
    }

    private static bool TryReadUniqueProperties(
        JsonElement element,
        out Dictionary<string, JsonElement> properties)
    {
        properties = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                properties.Clear();
                return false;
            }
        }

        return true;
    }

    private static bool IsBoundedText(
        string value,
        int maximumBytes,
        bool allowEmpty)
    {
        if ((!allowEmpty && value.Length == 0)
            || value.Contains('\0', StringComparison.Ordinal)
            || value.Any(character =>
                char.IsControl(character)
                && character is not '\t' and not '\r' and not '\n'))
        {
            return false;
        }

        return TryGetUtf8ByteCount(value, out var bytes)
            && bytes <= maximumBytes;
    }

    private static bool IsRole(string value) =>
        IsBoundedText(
            value,
            MaximumRoleBytes,
            allowEmpty: false)
        && value.All(character =>
            character is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-'
                or '_');

    private static bool TryGetUtf8ByteCount(
        string value,
        out int byteCount)
    {
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            byteCount = 0;
            return false;
        }
    }

    private static bool IsHandleIdentifier(
        string value,
        int maximumLength) =>
        value.Length is > 0
            && value.Length <= maximumLength
            && value.All(character =>
                character is >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '-'
                    or '_');

    private sealed class ActiveNativeNavigation(
        long generation,
        BrowserAddress? pendingAddress)
    {
        public long Generation { get; } = generation;

        public BrowserAddress? PendingAddress { get; set; } =
            pendingAddress;

        public bool HasStarted { get; set; }
    }
}
