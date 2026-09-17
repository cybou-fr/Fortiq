pub struct KeyInput {
    pub text: String,
    pub ctrl: bool,
    pub alt: bool,
    pub shift: bool,
}

pub fn encode_key(input: &KeyInput) -> Option<Vec<u8>> {
    // Ctrl combinations
    if input.ctrl {
        if let Some(c) = input.text.chars().next() {
            let lower = c.to_ascii_lowercase();
            if lower.is_ascii_lowercase() {
                let code = (lower as u8) - b'a' + 1;
                return Some(vec![code]);
            }
            match c {
                ' ' | '@' => return Some(vec![0]),
                '[' => return Some(vec![27]),
                '\\' => return Some(vec![28]),
                ']' => return Some(vec![29]),
                '^' => return Some(vec![30]),
                '_' => return Some(vec![31]),
                _ => {}
            }
        }
    }

    // Special keys
    match input.text.as_str() {
        "\r" | "\n" => Some(vec![b'\r']),
        "\x08" => Some(vec![0x7f]), // Backspace -> DEL
        "\t" => Some(vec![b'\t']),
        "\x1b" => Some(vec![0x1b]),                            // Escape
        "\u{F700}" | "\u{001b}[A" => Some(b"\x1b[A".to_vec()), // Up arrow
        "\u{F701}" | "\u{001b}[B" => Some(b"\x1b[B".to_vec()), // Down arrow
        "\u{F702}" | "\u{001b}[D" => Some(b"\x1b[D".to_vec()), // Left arrow
        "\u{F703}" | "\u{001b}[C" => Some(b"\x1b[C".to_vec()), // Right arrow
        "\u{F728}" | "\x7f" => Some(b"\x1b[3~".to_vec()),      // Delete
        "\u{F729}" => Some(b"\x1b[H".to_vec()),                // Home
        "\u{F72B}" => Some(b"\x1b[F".to_vec()),                // End
        "\u{F72C}" => Some(b"\x1b[5~".to_vec()),               // PageUp
        "\u{F72D}" => Some(b"\x1b[6~".to_vec()),               // PageDown
        text if !text.is_empty() => {
            if input.alt {
                let mut bytes = vec![0x1b];
                bytes.extend_from_slice(text.as_bytes());
                Some(bytes)
            } else {
                Some(text.as_bytes().to_vec())
            }
        }
        _ => None,
    }
}
