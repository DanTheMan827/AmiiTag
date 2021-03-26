//
//  KeyFiles.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 7/7/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation

class KeyFiles {
    enum KeyError: Swift.Error {
        case FileError
    }
    
    fileprivate static let documentsKeyPath = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("key_retail.bin")
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
    
    fileprivate static func LoadKeys() -> Bool {
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
