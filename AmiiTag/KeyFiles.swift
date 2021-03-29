//
//  KeyFiles.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 7/7/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation

class KeyFiles {
    static let validHash = Data([0x7f, 0x92, 0x83, 0x65, 0x4e, 0xc1, 0x09, 0x7f,
                                0xbd, 0xff, 0x31, 0xde, 0x94, 0x66, 0x51, 0xae,
                                0x60, 0xc2, 0x85, 0x4a, 0xfb, 0x54, 0x4a, 0xbe,
                                0x89, 0x63, 0xd3, 0x89, 0x63, 0x9c, 0x71, 0x0e])
    
    static func validateKeyFile(url: URL) -> Bool {
        guard let attr = try? FileManager.default.attributesOfItem(atPath: url.path) as NSDictionary,
              attr.fileSize() == 160,
              url.sha256()?.elementsEqual(KeyFiles.validHash) == true else {
            return false
        }
        
        return true
    }
    
    enum KeyError: Swift.Error {
        case FileError
    }
    
    static let documentsKeyPath = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("key_retail.bin")
    fileprivate static let internalKeyPath = Bundle.main.url(forResource: "key_retail", withExtension: "bin")
    
    fileprivate static var keyRetail: Data? {
        if internalKeyPath != nil && FileManager.default.fileExists(atPath: internalKeyPath!.path) {
            print("Loaded key_retail.bin from app bundle")
            
            return try? Data(contentsOf: internalKeyPath!)
        } else if FileManager.default.fileExists(atPath: documentsKeyPath.path) {
            print("Loaded key_retail.bin from documents")
            
            return try? Data(contentsOf: documentsKeyPath)
        }
        
        return nil
    }
    
    static var staticKey: TagKey? = nil
    static var dataKey: TagKey? = nil
    static var hasKeys: Bool {
        return (staticKey != nil && dataKey != nil) || LoadKeys()
    }
    
    static func LoadKeys() -> Bool {
        if staticKey != nil && dataKey != nil {
            return true
        }
        
        if let keyData = keyRetail {
            if keyData.count == 160 {
                dataKey = TagKey(data: Data(keyData[0..<80]))
                staticKey = TagKey(data: Data(keyData[80..<160]))
                
                return true
            }
        }
        
        return false
    }
}
