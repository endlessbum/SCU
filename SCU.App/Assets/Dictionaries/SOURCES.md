# Sources and maintenance notes

## English (`en.txt`)

The list was generated from the locally installed Hunspell `en_US.dic` dictionary associated with SCOWL, filtered to alphabetic English entries (Latin letters only), lowercased, de-duplicated, and merged with the previous SCU list. The associated licensing and attribution notices are preserved in `SCOWL-COPYRIGHT.txt`. Keep that notice distributed with the application. This file is a candidate-word lexicon, not a ranked frequency list.

## Russian (`ru.txt`)

The list combines the previous SCU list, manually curated common words and inflected forms, and Cyrillic tokens extracted from first-party SCU source/UI/documentation files (test files and generated/build/vendor directories excluded). It is normalized to lowercase, de-duplicated, sorted, and restricted to Russian letters `а-яё`. No external Russian corpus was bundled in this revision. A possible next source is [Goudron/ru-spelling-dictionary](https://github.com/Goudron/ru-spelling-dictionary), whose repository describes a 575,484-entry Hunspell dictionary under MPL-2.0 with an upstream BSD notice; its payload could not be retrieved into this working environment, so none of its data is included in `ru.txt`. The current list is not frequency-ranked and should be reviewed against a licensed Russian dictionary before claiming broad linguistic coverage.

## Runtime behavior

Both files are UTF-8 with one candidate word per line. The loader additionally validates each entry against the expected alphabet, preventing an accidental mixed-language or malformed line from entering the in-memory `HashSet`. Dictionary files are copied to normal build output, publish output, and the test output directory.
