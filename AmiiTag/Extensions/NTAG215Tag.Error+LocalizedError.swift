//
//  NTAG215Tag.Error+LocalizedError.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 3/28/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation

extension NTAG215Tag.Error: LocalizedError {
    public var errorDescription: String? {
        switch self {
        case .invalidTagType: return "Invalid tag type"
        case .unknownError: return "Unknown error"
        }
    }
}
